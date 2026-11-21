using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Owner.Be1.OwnerCalendarSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be1;

public sealed record CalendarMonth(
    IReadOnlyList<CalendarEntryRow> Entries,
    IReadOnlyList<BlockedRangeRow> Blocks,
    IReadOnlyList<CalendarSpecialPriceRow> SpecialPrices,
    IReadOnlyList<FestivalRow> Festivals,
    IReadOnlyList<MeterDto> Meters,
    WindowDto Window,
    int HiddenCount,
    CalendarReminderDto Reminder,
    bool SummaryAllowed,
    bool PaymentEnabled,
    DateOnly Today);

public sealed record CalendarUpcoming(IReadOnlyList<CalendarEntryRow> Items, string? NextCursor, int? HiddenCount, int? BeyondWindowAppCount, DateOnly? BeyondWindowFirstDate);

public sealed record CalendarPage(IReadOnlyList<CalendarEntryRow> Items, string? NextCursor);

public sealed record CalendarSummary(string Month, long FinalTotal, long ReceivedTotal, long BalanceTotal, int EntryCount, int DueCount,
    IReadOnlyList<CalendarEntryRow> TopBalances, bool PaymentEnabled);

// C1 to C4, C9, C10 (contract 2.1). Reads take no lock; every list is one projected statement on a calendar index after the
// owner context statement (which carries the active manual, block and special price counts as scalar subqueries).
public sealed class OwnerCalendarReader(ApplicationDbContext db, OwnerContextReader contexts, IMemoryCache cache)
{
    private static readonly TimeSpan FestivalTtl = TimeSpan.FromMinutes(10);
    private const int FestivalBackDays = 100, FestivalAheadDays = 400;

    public Task<OwnerContext?> ContextAsync(Guid userId, string kind, CancellationToken ct = default)
        => contexts.LoadAsync(userId, kind, ContextExtras(kind), null, ct);

    public static long ActiveManual(OwnerContext ctx) => ctx.Extra(1) ?? 0;

    public static long ActiveBlocks(OwnerContext ctx) => ctx.Extra(2) ?? 0;

    public static long ActiveSpecials(OwnerContext ctx) => ctx.Extra(3) ?? 0;

    public static IReadOnlyList<MeterDto> Meters(OwnerContext ctx, long? activeManual = null, long? activeBlocks = null)
        => OwnerCalendarRules.Meters(ctx.Kind, ctx.Limits, activeManual ?? ActiveManual(ctx), activeBlocks ?? ActiveBlocks(ctx), ActiveSpecials(ctx))
            .Select(m => new MeterDto(m.Key, m.Used, m.Limit)).ToList();

    public async Task<Be1Result<CalendarMonth>> MonthAsync(OwnerContext ctx, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var window = ctx.Window;
        var (verdict, f, t) = OwnerCalendarRules.MonthRange(window, from, to);
        switch (verdict)
        {
            case OwnerCalendarRules.RangeVerdict.BadOrder: return new Be1Error(OwnerErrorTypes.BadRequest, OwnerCalendarRules.RangeOrder);
            case OwnerCalendarRules.RangeVerdict.TooLong: return new Be1Error(OwnerErrorTypes.BadRequest, OwnerCalendarRules.RangeTooLong);
            case OwnerCalendarRules.RangeVerdict.Locked: return Be1Error.Of(OwnerErrorTypes.PlanFeatureLocked, new { key = PlanFeatureKeys.MonthsAhead });
            case OwnerCalendarRules.RangeVerdict.Outside: return new Be1Error(OwnerErrorTypes.BadRequest, OwnerCalendarRules.RangeOutside);
        }

        var limit = ctx.Limits.CalendarEntries;
        var list = (await db.Database.SqlQueryRaw<CalendarListRow>(Month,
            [.. Scope(ctx.Kind, ctx.ListingId), Date("from", f), Date("to", t), Date("today", ctx.Today), Int("limit", limit), Date("windowEnd", window.MonthTo)])
            .ToListAsync(ct))[0];
        var entries = CalendarEntryView.ParseRows(list.Items);

        IReadOnlyList<BlockedRangeRow> blocks = [];
        IReadOnlyList<CalendarSpecialPriceRow> specials = [];
        if (ctx.Kind == ListingKinds.Stay)
        {
            blocks = await db.Database.SqlQueryRaw<BlockedRangeRow>(Blocks, Uuid("l", ctx.ListingId), Date("from", f), Date("to", t)).ToListAsync(ct);
            specials = await db.Database.SqlQueryRaw<CalendarSpecialPriceRow>(SpecialPrices, Uuid("l", ctx.ListingId), Date("from", f), Date("to", t)).ToListAsync(ct);
        }
        var festivals = (await FestivalsAsync(ctx.StateName, ctx.Today, ct)).Where(x => x.DateTo >= f && x.DateFrom <= t).ToList();

        return Be1Result<CalendarMonth>.Success(new CalendarMonth(
            entries, blocks, specials, festivals, Meters(ctx), WindowDto.From(window),
            OwnerCalendarRules.HiddenCount(ActiveManual(ctx), limit, list.VisibleBeyond ?? 0),
            new CalendarReminderDto(ctx.Limits.Reminders, ctx.RemindersOn), ctx.Limits.MonthSummary, ctx.Limits.PaymentEnabled, ctx.Today));
    }

    public async Task<CalendarUpcoming> UpcomingAsync(OwnerContext ctx, (DateOnly Date, Guid Id)? cursor, CancellationToken ct = default)
    {
        var window = ctx.Window;
        var first = cursor == null;
        var limit = ctx.Limits.CalendarEntries;
        var list = (await db.Database.SqlQueryRaw<CalendarListRow>(Upcoming,
            [.. Scope(ctx.Kind, ctx.ListingId), Date("today", ctx.Today), Date("upcomingTo", window.UpcomingTo), Int("limit", limit),
             Date("cd", cursor?.Date), Uuid("cid", cursor?.Id), Int("take", OwnerLimits.PageSize + 1), Bool("first", first)])
            .ToListAsync(ct))[0];
        var rows = CalendarEntryView.ParseRows(list.Items);
        var (page, next) = Cut(rows, r => OwnerCursors.EncodeDate(r.DateFrom, r.Id));
        return new CalendarUpcoming(page, next,
            first ? OwnerCalendarRules.HiddenCount(ActiveManual(ctx), limit, list.VisibleBeyond ?? 0) : null,
            first ? (int)(list.AutoBeyond ?? 0) : null,
            first ? list.AutoBeyondFirst : null);
    }

    public async Task<CalendarPage> PastAsync(OwnerContext ctx, (DateOnly Date, Guid Id)? cursor, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<CalendarEntryRow>(Past,
            [.. Scope(ctx.Kind, ctx.ListingId), Date("today", ctx.Today), Date("pastFrom", ctx.Window.PastFrom),
             Date("cd", cursor?.Date), Uuid("cid", cursor?.Id), Int("take", OwnerLimits.PageSize + 1)])
            .ToListAsync(ct);
        var (page, next) = Cut(rows, r => OwnerCursors.EncodeDate(r.DateTo, r.Id));
        return new CalendarPage(page, next);
    }

    public async Task<CalendarEntryRow?> EntryAsync(OwnerContext ctx, Guid id, CancellationToken ct = default)
        => await ReadOneAsync(db, ctx.Kind, ctx.ListingId, id, ct);

    public static async Task<CalendarEntryRow?> ReadOneAsync(ApplicationDbContext db, string kind, Guid listingId, Guid id, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<CalendarEntryRow>(One, [.. Scope(kind, listingId), Uuid("id", id)]).ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<Be1Result<CalendarSummary>> SummaryAsync(OwnerContext ctx, string? month, CancellationToken ct = default)
    {
        if (!ctx.Limits.MonthSummary) return Be1Error.Of(OwnerErrorTypes.PlanFeatureLocked, new { key = PlanFeatureKeys.MonthSummary });
        if (OwnerCalendarRules.Month(month, ctx.Window) is not { } m) return new Be1Error(OwnerErrorTypes.BadRequest, OwnerCalendarRules.MonthOutside);
        var row = (await db.Database.SqlQueryRaw<CalendarSummaryRow>(Summary,
            [.. Scope(ctx.Kind, ctx.ListingId), Date("m1", m.Start), Date("m2", m.End)]).ToListAsync(ct))[0];
        return Be1Result<CalendarSummary>.Success(new CalendarSummary(m.Start.ToString("yyyy-MM"), row.FinalTotal, row.ReceivedTotal, row.BalanceTotal,
            row.EntryCount, row.DueCount, CalendarEntryView.ParseRows(row.Top), ctx.Limits.PaymentEnabled));
    }

    public async Task<Be1Result<CalendarPage>> BalanceDueAsync(OwnerContext ctx, string? month, (long Value, Guid Id)? cursor, CancellationToken ct = default)
    {
        if (!ctx.Limits.MonthSummary) return Be1Error.Of(OwnerErrorTypes.PlanFeatureLocked, new { key = PlanFeatureKeys.MonthSummary });
        if (OwnerCalendarRules.Month(month, ctx.Window) is not { } m) return new Be1Error(OwnerErrorTypes.BadRequest, OwnerCalendarRules.MonthOutside);
        var rows = await db.Database.SqlQueryRaw<CalendarEntryRow>(BalanceDue,
            [.. Scope(ctx.Kind, ctx.ListingId), Date("m1", m.Start), Date("m2", m.End), Long("cb", cursor?.Value), Uuid("cid", cursor?.Id),
             Int("take", OwnerLimits.PageSize + 1)])
            .ToListAsync(ct);
        var (page, next) = Cut(rows, r => OwnerCursors.EncodeNumber(MoneyNoteMath.Balance(r.Money) ?? 0, r.Id));
        return Be1Result<CalendarPage>.Success(new CalendarPage(page, next));
    }

    // Festivals of the owner's state (or all India) from today - 100 to today + 400 days, kept in memory for 10 minutes.
    private async Task<IReadOnlyList<FestivalRow>> FestivalsAsync(string? stateName, DateOnly today, CancellationToken ct)
    {
        var key = $"owner:calendar:festivals:{today:yyyyMMdd}:{stateName ?? "*"}";
        if (cache.TryGetValue(key, out IReadOnlyList<FestivalRow>? cached) && cached != null) return cached;
        var rows = await db.Database.SqlQueryRaw<FestivalRow>(Festivals,
            Date("from", today.AddDays(-FestivalBackDays)), Date("to", today.AddDays(FestivalAheadDays)), Text("state", stateName)).ToListAsync(ct);
        cache.Set(key, (IReadOnlyList<FestivalRow>)rows, FestivalTtl);
        return rows;
    }

    private static (List<T> Page, string? Next) Cut<T>(List<T> rows, Func<T, string> cursorOf)
    {
        if (rows.Count <= OwnerLimits.PageSize) return (rows, null);
        var page = rows.Take(OwnerLimits.PageSize).ToList();
        return (page, cursorOf(page[^1]));
    }
}
