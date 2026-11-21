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

// C5 to C7 and C11 (contract 2.1, 3.2, 4.1, 4.2). Create: the client id is the op id; the listing row lock (level 5) serialises the
// limit check, so two phones saving the last free slot get one 200 and one 409. Edit and delete are version-guarded or idempotent
// single statements; editing is never limited, but the dates must stay inside the plan window.
public sealed class OwnerCalendarWriter(ApplicationDbContext db, ILogger<OwnerCalendarWriter> logger)
{
    public async Task<Be1Result<CalendarEntryWrite>> CreateAsync(OwnerContext ctx, Guid id, ManualEntryInput input, CancellationToken ct = default)
    {
        if (id == Guid.Empty) return new Be1Error(OwnerErrorTypes.BadRequest, "Missing id.");
        if (OwnerCalendarRules.Validate(ctx.Kind, input) is { } invalid) return new Be1Error(invalid.Type, invalid.Message);
        if (!OwnerCalendarRules.InsideWindow(ctx.Window, input.DateFrom, input.DateTo)) return Be1Error.Of(OwnerErrorTypes.CalendarOutsideWindow);

        return await PgRetry.OnceAsync<Be1Result<CalendarEntryWrite>>(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            if (!await OwnerLocks.LockListingAsync(db, ctx.Kind, ctx.ListingId, c)) return Be1Error.Of(OwnerErrorTypes.ListingNotFound);

            var check = (await db.Database.SqlQueryRaw<CalendarCreateCheckRow>(CreateCheck,
                [.. Scope(ctx.Kind, ctx.ListingId), Date("today", ctx.Today), Uuid("id", id)]).ToListAsync(c))[0];
            if (CalendarEntryView.ParseRow(check.Existing) is { } existing)
                return await ReplayAsync(ctx, existing, check.Active, tx, c);

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
            var overlap = await OverlapAsync(ctx, inserted[0], c);
            await tx.CommitAsync(c);
            return Be1Result<CalendarEntryWrite>.Success(new CalendarEntryWrite(inserted[0], OwnerCalendarReader.Meters(ctx, overlap.Active), ToOverlap(overlap)));
        }, ct, logger);
    }

    public async Task<Be1Result<CalendarEntryWrite>> UpdateAsync(OwnerContext ctx, Guid id, int? version, ManualEntryInput input, CancellationToken ct = default)
    {
        if (version is not { } v) return new Be1Error(OwnerErrorTypes.BadRequest, "Missing version.");
        if (OwnerCalendarRules.Validate(ctx.Kind, input) is { } invalid) return new Be1Error(invalid.Type, invalid.Message);
        if (!OwnerCalendarRules.InsideWindow(ctx.Window, input.DateFrom, input.DateTo)) return Be1Error.Of(OwnerErrorTypes.CalendarOutsideWindow);

        var rows = await db.Database.SqlQueryRaw<CalendarEntryRow>(Update, [.. InsertParameters(ctx, id, input), Int("v", v)]).ToListAsync(ct);
        CalendarEntryRow saved;
        if (rows.Count == 1)
        {
            saved = rows[0];
        }
        else
        {
            var current = await OwnerCalendarReader.ReadOneAsync(db, ctx.Kind, ctx.ListingId, id, ct);
            if (current == null) return NotFound();
            if (!current.IsManual) return Be1Error.Of(OwnerErrorTypes.EntryNotManual);
            if (!OwnerCalendarRules.SameManual(current, input)) return Stale(current);
            saved = current;
        }
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
    ];
}
