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

    public async Task<IReadOnlyList<FestivalDto>> GetAsync(ApplicationDbContext db, string? stateName, DateOnly today, CancellationToken ct = default)
    {
        var key = Key(stateName, today);
        if (cache.TryGetValue(key, out IReadOnlyList<FestivalDto>? hit) && hit != null) return hit;
        var (from, to) = Window(today);
        IReadOnlyList<FestivalDto> rows = await Query(db, stateName, from, to).ToListAsync(ct);
        cache.Set(key, rows, Ttl);
        return rows;
    }

    // ix_festivaldates_active_from. stateName null = all-India rows only.
    public static IQueryable<FestivalDto> Query(ApplicationDbContext db, string? stateName, DateOnly from, DateOnly to)
        => db.Set<FestivalDate>().AsNoTracking()
            .Where(f => f.IsActive && f.DateTo >= from && f.DateFrom <= to && (f.StateName == null || f.StateName == stateName))
            .OrderBy(f => f.DateFrom).ThenBy(f => f.Id)
            .Select(f => new FestivalDto(f.Id, f.Name, f.DateFrom, f.DateTo));

    public static IReadOnlyList<FestivalDto> Slice(IReadOnlyList<FestivalDto> rows, DateOnly from, DateOnly to)
        => rows.Where(f => f.DateTo >= from && f.DateFrom <= to).ToList();
}
