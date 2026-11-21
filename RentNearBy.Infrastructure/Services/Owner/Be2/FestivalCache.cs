using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Be2;

// F1 cache (contract 2.3): active festivals of one state (plus all-India rows) from 90 days back to 400 days ahead, 1 hour in
// IMemoryCache. Every admin write calls Drop (a version bump, so no key scan); single API instance like AppTabCache.
public sealed class FestivalCache(IMemoryCache cache)
{
    public const int BackDays = OwnerLimits.PastDays;
    public const int AheadDays = 400;
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private long _version;

    public void Drop() => Interlocked.Increment(ref _version);

    public static (DateOnly From, DateOnly To) Window(DateOnly today) => (today.AddDays(-BackDays), today.AddDays(AheadDays));

    // Keyed by the exact name the statement compares, so another spelling never fills the key of a real state.
    public string Key(string? stateName, DateOnly today)
        => $"owner:festivals:{Interlocked.Read(ref _version)}:{today:yyyyMMdd}:{stateName?.Trim() ?? "*"}";

    // One cached row: the F1 fields plus the state name the Home festival card shows (null = all India).
    public sealed record Row(Guid Id, string Name, DateOnly DateFrom, DateOnly DateTo, string? StateName)
    {
        public FestivalDto ToDto() => new(Id, Name, DateFrom, DateTo);
    }

    public async Task<IReadOnlyList<FestivalDto>> GetAsync(ApplicationDbContext db, string? stateName, DateOnly today, CancellationToken ct = default)
        => (await GetRowsAsync(db, stateName, today, ct)).Select(r => r.ToDto()).ToList();

    public async Task<IReadOnlyList<Row>> GetRowsAsync(ApplicationDbContext db, string? stateName, DateOnly today, CancellationToken ct = default)
    {
        var key = Key(stateName, today);
        if (cache.TryGetValue(key, out IReadOnlyList<Row>? hit) && hit != null) return hit;
        var (from, to) = Window(today);
        IReadOnlyList<Row> rows = await RowQuery(db, stateName, from, to).ToListAsync(ct);
        cache.Set(key, rows, Ttl);
        return rows;
    }

    private static IQueryable<Row> RowQuery(ApplicationDbContext db, string? stateName, DateOnly from, DateOnly to)
        => db.Set<FestivalDate>().AsNoTracking()
            .Where(f => f.IsActive && f.DateTo >= from && f.DateFrom <= to && (f.StateName == null || f.StateName == stateName))
            .OrderBy(f => f.DateFrom).ThenBy(f => f.Id)
            .Select(f => new Row(f.Id, f.Name, f.DateFrom, f.DateTo, f.StateName));

    // ix_festivaldates_active_from. stateName null = all-India rows only.
    public static IQueryable<FestivalDto> Query(ApplicationDbContext db, string? stateName, DateOnly from, DateOnly to)
        => db.Set<FestivalDate>().AsNoTracking()
            .Where(f => f.IsActive && f.DateTo >= from && f.DateFrom <= to && (f.StateName == null || f.StateName == stateName))
            .OrderBy(f => f.DateFrom).ThenBy(f => f.Id)
            .Select(f => new FestivalDto(f.Id, f.Name, f.DateFrom, f.DateTo));

    // F1 takes a state name from any signed-in user: only a name of the district list keeps its own key, any other answers the
    // all-India list (the same rows the query would give it). The state list is read once a day.
    public const string StatesKey = "owner:festivals:states";
    public static readonly TimeSpan StatesTtl = TimeSpan.FromHours(24);

    public async Task<string?> KnownStateAsync(ApplicationDbContext db, string? stateName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stateName)) return null;
        if (!cache.TryGetValue(StatesKey, out IReadOnlySet<string>? states) || states == null)
        {
            var names = await db.Districts.AsNoTracking()
                .Where(d => d.StateName != null && d.StateName != "")
                .Select(d => d.StateName!)
                .Distinct()
                .ToListAsync(ct);
            states = names.ToHashSet(StringComparer.Ordinal);
            cache.Set(StatesKey, states, StatesTtl);
        }
        return ResolveState(states, stateName);
    }

    public static string? ResolveState(IReadOnlySet<string> states, string? stateName)
    {
        var name = string.IsNullOrWhiteSpace(stateName) ? null : stateName.Trim();
        return name != null && states.Contains(name) ? name : null;
    }

    public static IReadOnlyList<FestivalDto> Slice(IReadOnlyList<FestivalDto> rows, DateOnly from, DateOnly to)
        => rows.Where(f => f.DateTo >= from && f.DateFrom <= to).ToList();
}
