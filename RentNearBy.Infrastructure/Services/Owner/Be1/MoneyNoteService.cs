using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Owner.Be1.OwnerCalendarSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be1;

// C8 (doc 6.2): private money notes on any entry of the caller's listing, Auto or Manual, cancelled or ended too. Never limited by
// the plan, except that a manual entry hidden by the plan answers 404 (D-30).
// One version-guarded statement; a 0-row answer re-reads once to choose 404, a replay (same values) or 409 StaleEdit.
public sealed class MoneyNoteService(ApplicationDbContext db)
{
    public async Task<Be1Result<CalendarEntryRow>> SetAsync(OwnerContext ctx, Guid id, int? version, MoneyNote note, CancellationToken ct = default)
    {
        if (version is not { } v) return new Be1Error(OwnerErrorTypes.BadRequest, "Missing version.");
        if (OwnerCalendarRules.ValidateMoney(note) is { } invalid) return new Be1Error(invalid.Type, invalid.Message);

        var rows = await db.Database.SqlQueryRaw<CalendarEntryRow>(Money,
            [.. Scope(ctx.Kind, ctx.ListingId), Uuid("id", id), Int("v", v), Date("today", ctx.Today), Int("limit", ctx.Limits.CalendarEntries),
             Int("final", note.FinalAmount), Int("advanceReceived", note.AdvanceReceived), Bool("paid", note.PaidInFull)]).ToListAsync(ct);
        if (rows.Count == 1) return Be1Result<CalendarEntryRow>.Success(rows[0]);

        var current = await OwnerCalendarReader.ReadVisibleAsync(db, ctx, id, ct);
        if (current == null) return OwnerCalendarWriter.NotFound();
        return OwnerCalendarRules.SameMoney(current, note) ? Be1Result<CalendarEntryRow>.Success(current) : OwnerCalendarWriter.Stale(current);
    }
}
