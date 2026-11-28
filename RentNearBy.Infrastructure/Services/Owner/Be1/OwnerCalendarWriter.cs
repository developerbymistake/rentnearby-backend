using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Owner.Be1.OwnerCalendarSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be1;

public sealed record CalendarEntryWrite(CalendarEntryRow Entry, IReadOnlyList<MeterDto> Meters, CalendarOverlapDto Overlap);

// C5 to C7 and C11 (contract 2.1, 3.2, 4.1, 4.2). Create: the client id is the op id; the owner's Users row in KEY SHARE (level 2,
// before the listing, so it cannot deadlock with account deletion) then the listing row lock (level 5) serialises the limit check,
// so two phones saving the last free slot get one 200 and one 409. Edit and delete are version-guarded or idempotent single statements.
// Edit (D-07, FB-04): an ended entry is read-only except its money note, so no edit can revive a row and edits need no limit
// check; a date that does not move always passes, a moved date must be inside the manual window; an entry above the plan limit is 409 PlanRowInactive (D-30).
// Create also stops at the kept-rows ceiling (OwnerLimits.ManualRowsCeiling), so past-dated rows cannot be spammed.
// I-25 (taxi only): a manual trip may land on a day off; a hiding trip (not KeepVisible) removes that day off on create and edit
// under the taxi row lock (a taxi edit is one transaction, a refusal rolls back). A KeepVisible trip keeps the day off, the same
// rule as the day-off save (allowed beside a KeepVisible trip). KeepVisible is taxi-only.
public sealed class OwnerCalendarWriter(ApplicationDbContext db, ILogger<OwnerCalendarWriter> logger)
{
    public async Task<Be1Result<CalendarEntryWrite>> CreateAsync(OwnerContext ctx, Guid id, ManualEntryInput input, CancellationToken ct = default)
    {
        input = ForKind(ctx.Kind, input);
        if (id == Guid.Empty) return new Be1Error(OwnerErrorTypes.BadRequest, "Missing id.");
        if (OwnerCalendarRules.Validate(ctx.Kind, input) is { } invalid) return new Be1Error(invalid.Type, invalid.Message);
        if (!OwnerCalendarRules.InsideWindow(ctx.Window, input.DateFrom, input.DateTo)) return Be1Error.Of(OwnerErrorTypes.CalendarOutsideWindow);

        return await PgRetry.OnceAsync<Be1Result<CalendarEntryWrite>>(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            if (!await OwnerLocks.KeyShareUserAsync(db, ctx.UserId, c)
                || !await OwnerLocks.LockListingAsync(db, ctx.Kind, ctx.ListingId, c)) return Be1Error.Of(OwnerErrorTypes.ListingNotFound);
            // FB-38: the limit and the window are decided by the plan as it is under the locks, not as it was before them.
            ctx = await OwnerContextReader.WithLimitsUnderLockAsync(db, ctx, c);
            if (!OwnerCalendarRules.InsideWindow(ctx.Window, input.DateFrom, input.DateTo)) return Be1Error.Of(OwnerErrorTypes.CalendarOutsideWindow);

            var check = (await db.Database.SqlQueryRaw<CalendarCreateCheckRow>(CreateCheck,
                [.. Scope(ctx.Kind, ctx.ListingId), Date("today", ctx.Today), Date("keptFrom", ctx.Window.PastFrom), Uuid("id", id)]).ToListAsync(c))[0];
            if (CalendarEntryView.ParseRow(check.Existing) is { } existing)
                return await ReplayAsync(ctx, existing, check.Active, tx, c);
            if (check.Total >= OwnerLimits.ManualRowsCeiling)
                return new Be1Error(OwnerErrorTypes.CalendarEntryLimit, OwnerErrorTypes.LimitMessage(OwnerErrorTypes.CalendarEntryLimit, OwnerLimits.ManualRowsCeiling, false),
                    new PlanLimitRefusalDto(PlanFeatureKeys.CalendarEntries, OwnerLimits.ManualRowsCeiling, (int)check.Total, false));

            var limit = ctx.Limits.CalendarEntries;
            if (check.Active >= limit)
                return new Be1Error(OwnerErrorTypes.CalendarEntryLimit, OwnerErrorTypes.LimitMessage(OwnerErrorTypes.CalendarEntryLimit, limit, ctx.Limits.CanUpgrade),
                    new PlanLimitRefusalDto(PlanFeatureKeys.CalendarEntries, limit, (int)check.Active, ctx.Limits.CanUpgrade));

            var inserted = await db.Database.SqlQueryRaw<CalendarEntryRow>(Insert, InsertParameters(ctx, id, input)).ToListAsync(c);
            if (inserted.Count == 0)
            {
                // A parallel replay of the same id committed between the check and the insert.
                var raced = await OwnerCalendarReader.ReadOneAsync(db, ctx.Kind, ctx.ListingId, id, c);
                if (raced is not { IsManual: true }) return Be1Error.Of(OwnerErrorTypes.IdTaken);
                return await ReplayAsync(ctx, raced, check.Active, tx, c);
            }
            if (ctx.Kind == ListingKinds.Taxi && !inserted[0].KeepVisible) await ClearDaysOffAsync(ctx, inserted[0], c);
            var overlap = await OverlapAsync(ctx, inserted[0], c);
            await tx.CommitAsync(c);
            return Be1Result<CalendarEntryWrite>.Success(new CalendarEntryWrite(inserted[0], OwnerCalendarReader.Meters(ctx, overlap.Active), ToOverlap(overlap)));
        }, ct, logger);
    }

    public async Task<Be1Result<CalendarEntryWrite>> UpdateAsync(OwnerContext ctx, Guid id, int? version, ManualEntryInput input, CancellationToken ct = default)
    {
        if (version is not { } v) return new Be1Error(OwnerErrorTypes.BadRequest, "Missing version.");
        input = ForKind(ctx.Kind, input);
        if (OwnerCalendarRules.Validate(ctx.Kind, input) is { } invalid) return new Be1Error(invalid.Type, invalid.Message);
        if (ctx.Kind != ListingKinds.Taxi) return (await UpdateRowAsync(ctx, id, v, input, ct)).Result;

        // Taxi: the taxi row lock (the one a day-off save takes), so the edit and the day-off clear commit together; a refusal rolls back.
        return await PgRetry.OnceAsync<Be1Result<CalendarEntryWrite>>(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            if (!await OwnerLocks.LockTaxiAsync(db, ctx.ListingId, c)) return Be1Error.Of(OwnerErrorTypes.ListingNotFound);
            var (result, changed) = await UpdateRowAsync(ctx, id, v, input, c);
            if (!result.Ok) return result;
            // Contract item 3: a saved hiding trip ends any day off on its days (a same-content replay writes nothing).
            if (changed && !result.Value!.Entry.KeepVisible) await ClearDaysOffAsync(ctx, result.Value!.Entry, c);
            await tx.CommitAsync(c);
            return result;
        }, ct, logger);
    }

    // The version-guarded single statement. Changed = a row was written (not a replay of the same content).
    private async Task<(Be1Result<CalendarEntryWrite> Result, bool Changed)> UpdateRowAsync(
        OwnerContext ctx, Guid id, int v, ManualEntryInput input, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<CalendarEntryRow>(Update,
            [.. InsertParameters(ctx, id, input), Int("v", v), Date("today", ctx.Today), Int("limit", ctx.Limits.CalendarEntries),
             Bool("fromOk", OwnerCalendarRules.InWindow(ctx.Window, input.DateFrom)), Bool("toOk", OwnerCalendarRules.InWindow(ctx.Window, input.DateTo))])
            .ToListAsync(ct);
        if (rows.Count == 1) return (await WrittenAsync(ctx, rows[0], ct), true);
        return (await NotUpdatedAsync(ctx, id, v, input, ct), false);
    }

    // The success path is one statement; only a refusal re-reads to choose its answer.
    private async Task<Be1Result<CalendarEntryWrite>> NotUpdatedAsync(OwnerContext ctx, Guid id, int v, ManualEntryInput input, CancellationToken ct)
    {
        var current = await OwnerCalendarReader.ReadOneAsync(db, ctx.Kind, ctx.ListingId, id, ct);
        if (current == null) return NotFound();
        if (!current.IsManual) return Be1Error.Of(OwnerErrorTypes.EntryNotManual);
        if (OwnerCalendarRules.Ended(current.DateTo, ctx.Today))
            return new Be1Error(OwnerErrorTypes.EntryEnded, OwnerErrorTypes.EntryEndedMessage(ctx.Kind));
        if (await OwnerCalendarReader.ReadVisibleAsync(db, ctx, id, ct) == null) return Be1Error.Of(OwnerErrorTypes.PlanRowInactive);
        if (OwnerCalendarRules.SameManual(current, input)) return await WrittenAsync(ctx, current, ct);
        if (current.Version != v) return Stale(current);
        return Be1Error.Of(OwnerErrorTypes.CalendarOutsideWindow);
    }

    private async Task<Be1Result<CalendarEntryWrite>> WrittenAsync(OwnerContext ctx, CalendarEntryRow saved, CancellationToken ct)
    {
        var overlap = await OverlapAsync(ctx, saved, ct);
        return Be1Result<CalendarEntryWrite>.Success(new CalendarEntryWrite(saved, OwnerCalendarReader.Meters(ctx, overlap.Active), ToOverlap(overlap)));
    }

    // Idempotent: a row already gone is a success too. An Auto row cannot be deleted here.
    public async Task<Be1Error?> DeleteAsync(OwnerContext ctx, Guid id, CancellationToken ct = default)
    {
        var row = (await db.Database.SqlQueryRaw<CalendarDeleteRow>(Delete, [.. Scope(ctx.Kind, ctx.ListingId), Uuid("id", id)]).ToListAsync(ct))[0];
        return row.Deleted == 0 && row.IsAuto ? Be1Error.Of(OwnerErrorTypes.EntryNotManual) : null;
    }

    // C11. Turning reminders on needs the plan switch; turning them off is always allowed.
    public async Task<Be1Result<CalendarReminderDto>> SetReminderAsync(OwnerContext ctx, bool on, CancellationToken ct = default)
    {
        if (on && !ctx.Limits.Reminders) return Be1Error.Of(OwnerErrorTypes.PlanFeatureLocked, new { key = PlanFeatureKeys.Reminders });
        await db.Database.ExecuteSqlRawAsync(Reminder(ctx.Kind), [Bool("on", on), Uuid("l", ctx.ListingId)], ct);
        return Be1Result<CalendarReminderDto>.Success(new CalendarReminderDto(ctx.Limits.Reminders, on));
    }

    public static Be1Error NotFound() => new(Be1ErrorTypes.NotFound, "Entry not found.");

    public static Be1Error DayIsOff(DateOnly date) => Be1Error.Of(OwnerErrorTypes.DayIsOff, new { date = TaxiSearchDate.Text(date) });

    private Task<int> ClearDaysOffAsync(OwnerContext ctx, CalendarEntryRow row, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(ClearTaxiDaysOff, [Uuid("l", ctx.ListingId), Date("from", row.DateFrom), Date("to", row.DateTo)], ct);

    // KeepVisible belongs to taxi rows only; a stay write never carries it.
    public static ManualEntryInput ForKind(string kind, ManualEntryInput input)
        => kind == ListingKinds.Taxi || !input.KeepVisible ? input : input with { KeepVisible = false };

    public static Be1Error Stale(CalendarEntryRow current) => new(OwnerErrorTypes.StaleEdit, OwnerErrorTypes.MessageOf(OwnerErrorTypes.StaleEdit), null, current);

    private async Task<Be1Result<CalendarEntryWrite>> ReplayAsync(
        OwnerContext ctx, CalendarEntryRow existing, long active, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken ct)
    {
        if (existing.ListingKind != ctx.Kind || existing.ListingId != ctx.ListingId || !existing.IsManual) return Be1Error.Of(OwnerErrorTypes.IdTaken);
        var overlap = await OverlapAsync(ctx, existing, ct);
        await tx.CommitAsync(ct);
        return Be1Result<CalendarEntryWrite>.Success(new CalendarEntryWrite(existing, OwnerCalendarReader.Meters(ctx, overlap.Active), ToOverlap(overlap)));
    }

    private async Task<CalendarOverlapRow> OverlapAsync(OwnerContext ctx, CalendarEntryRow row, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<CalendarOverlapRow>(OwnerCalendarSql.Overlap(ctx.Kind),
            [.. Scope(ctx.Kind, ctx.ListingId), Date("today", ctx.Today), Uuid("id", row.Id), Date("from", row.DateFrom), Date("to", row.DateTo)])
            .ToListAsync(ct))[0];

    private static CalendarOverlapDto ToOverlap(CalendarOverlapRow r) => new((int)r.Bookings, (int)r.Blocked);

    private static object[] InsertParameters(OwnerContext ctx, Guid id, ManualEntryInput input) =>
    [
        Uuid("id", id), Uuid("me", ctx.UserId), Text("k", ctx.Kind), Uuid("l", ctx.ListingId),
        Date("from", input.DateFrom), Date("to", input.DateTo), Time("time", input.TimeOfDay),
        Text("fromPlace", input.FromPlace), Text("toPlace", input.ToPlace), Text("label", input.Label.Trim()), Text("note", input.Note),
        Int("final", input.Money.FinalAmount), Int("advanceReceived", input.Money.AdvanceReceived), Bool("paid", input.Money.PaidInFull),
        Bool("keepVisible", input.KeepVisible),
    ];
}
