using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class TripRepository(ApplicationDbContext context) : Repository<Trip>(context), ITripRepository
{
    public async Task<Trip?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId)
        => await _dbSet.FirstOrDefaultAsync(t => t.Id == id && t.OwnerUserId == ownerUserId);

    public async Task<(IReadOnlyList<Trip> Items, bool HasMore)> GetByOwnerPagedAsync(Guid ownerUserId, int page, int pageSize)
    {
        var size = Math.Max(1, pageSize);
        var skip = (Math.Max(1, page) - 1) * size;

        var rows = await _dbSet.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerUserId)
            .OrderByDescending(t => t.UpdatedAt)
            .ThenByDescending(t => t.Id)
            .Skip(skip)
            .Take(size + 1)
            .ToListAsync();

        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(size);
        return (rows, hasMore);
    }

    public async Task<(IReadOnlyList<Trip> Items, bool HasMore)> GetByOwnerPagedAsync(Guid ownerUserId, int page, int pageSize, bool completed)
    {
        var size = Math.Max(1, pageSize);
        var skip = (Math.Max(1, page) - 1) * size;

        var query = _dbSet.AsNoTracking().Where(t => t.OwnerUserId == ownerUserId);
        query = completed
            ? query.Where(t => t.Status == TripStatuses.Completed)
            : query.Where(t => t.Status != TripStatuses.Completed);

        var rows = await query
            .OrderByDescending(t => t.UpdatedAt)
            .ThenByDescending(t => t.Id)
            .Skip(skip)
            .Take(size + 1)
            .ToListAsync();

        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(size);
        return (rows, hasMore);
    }

    public async Task<(int Upcoming, int Completed)> GetStateCountsAsync(Guid ownerUserId)
    {
        var rows = await _dbSet.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerUserId)
            .GroupBy(t => t.Status == TripStatuses.Completed)
            .Select(g => new { Completed = g.Key, Count = g.Count() })
            .ToListAsync();
        return (rows.Where(r => !r.Completed).Sum(r => r.Count), rows.Where(r => r.Completed).Sum(r => r.Count));
    }

    public async Task<IReadOnlyList<Trip>> GetReminderCandidatesAsync(DateOnly startFrom, DateOnly startTo, DateOnly endFrom, DateOnly endTo, int take)
        => await _dbSet.AsNoTracking()
            .Where(t => t.Status == TripStatuses.Planned && t.MutedAt == null
                && ((t.StartDate != null && t.StartDate >= startFrom && t.StartDate <= startTo)
                    || (t.EndDate != null && t.EndDate >= endFrom && t.EndDate <= endTo)))
            .OrderBy(t => t.StartDate)
            .ThenBy(t => t.Id)
            .Take(take)
            .ToListAsync();

    // One-city trips never reach the page, nor trips whose trv key for this date is already logged.
    public async Task<(IReadOnlyList<Trip> Items, Guid? Next)> GetTravelEveCandidatesAsync(DateOnly travelDate, Guid? after, int take)
    {
        var size = Math.Max(1, take);
        var status = TripStatuses.Planned;
        var keyPrefix = "trv:";
        var keySuffix = $":{travelDate:yyyyMMdd}";
        var ids = await _context.Database
            .SqlQuery<Guid>($"""
                SELECT t."Id" AS "Value" FROM "Trips" t
                WHERE t."Status" = {status} AND t."MutedAt" IS NULL
                  AND t."StartDate" IS NOT NULL AND t."EndDate" IS NOT NULL
                  AND t."StartDate" <= {travelDate} AND t."EndDate" >= {travelDate}
                  AND (CASE WHEN jsonb_typeof(t."PlanJson" -> 'legs') = 'array'
                            THEN jsonb_array_length(t."PlanJson" -> 'legs') ELSE 0 END) > 1
                  AND NOT EXISTS (SELECT 1 FROM "TripPushLogs" p
                                  WHERE p."DedupeKey" = {keyPrefix} || t."Id"::text || {keySuffix})
                  AND ({after}::uuid IS NULL OR t."Id" > {after}::uuid)
                ORDER BY t."Id"
                LIMIT {size}
                """)
            .ToListAsync();
        if (ids.Count == 0) return (Array.Empty<Trip>(), null);

        var trips = await _dbSet.AsNoTracking().Where(t => ids.Contains(t.Id)).ToListAsync();
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return (trips.OrderBy(t => order[t.Id]).ToList(), ids.Count == size ? ids[^1] : (Guid?)null);
    }

    public async Task<IReadOnlyList<Trip>> GetAutoCompleteCandidatesAsync(DateOnly endBefore, int take)
        => await _dbSet.AsNoTracking()
            .Where(t => t.Status == TripStatuses.Planned && t.EndDate != null && t.EndDate <= endBefore)
            .OrderBy(t => t.EndDate)
            .ThenBy(t => t.Id)
            .Take(take)
            .ToListAsync();

    public async Task<int> TryCompleteAsync(Guid id, DateTime nowUtc)
        => await _dbSet
            .Where(t => t.Id == id && t.Status == TripStatuses.Planned)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TripStatuses.Completed)
                .SetProperty(t => t.CompletedAt, nowUtc)
                .SetProperty(t => t.UpdatedAt, nowUtc));

    public async Task<TripCityInfo?> FindCitySnapshotAsync(Guid ownerUserId, long cityKey)
        => await _dbSet.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerUserId && t.CityKey == cityKey)
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new TripCityInfo
            {
                CityKey = t.CityKey,
                Name = t.CityName,
                Latitude = t.CityLatitude,
                Longitude = t.CityLongitude,
            })
            .FirstOrDefaultAsync();

    // Row city first; else a later city of one of the caller's trips (fallback path only, no index).
    public async Task<TripCityInfo?> FindLegSnapshotAsync(Guid ownerUserId, long cityKey)
    {
        if (await FindCitySnapshotAsync(ownerUserId, cityKey) is { } row) return row;

        var probe = $"{{\"legs\":[{{\"cityKey\":{cityKey}}}]}}";
        var json = (await _context.Database
                .SqlQuery<string>($"""
                    SELECT "PlanJson" AS "Value" FROM "Trips"
                    WHERE "OwnerUserId" = {ownerUserId} AND "PlanJson" @> {probe}::jsonb
                    ORDER BY "UpdatedAt" DESC
                    LIMIT 1
                    """)
                .ToListAsync())
            .FirstOrDefault();
        if (json == null) return null;

        var plan = TripPlanReader.Parse(json);
        plan.Normalize();
        var leg = plan.Legs!.FirstOrDefault(l => l.CityKey == cityKey);
        return leg == null
            ? null
            : new TripCityInfo { CityKey = leg.CityKey, Name = leg.CityName, Latitude = leg.Latitude, Longitude = leg.Longitude };
    }

    public async Task<Trip?> FindByClientIdAsync(Guid ownerUserId, Guid clientTripId)
        => await _dbSet.FirstOrDefaultAsync(t => t.OwnerUserId == ownerUserId && t.ClientTripId == clientTripId);

    public async Task<List<Trip>> GetByIdsForOwnerAsync(IReadOnlyCollection<Guid> ids, Guid ownerUserId)
    {
        if (ids.Count == 0) return new List<Trip>();
        var list = ids.Distinct().ToList();
        return await _dbSet.AsNoTracking()
            .Where(t => list.Contains(t.Id) && t.OwnerUserId == ownerUserId)
            .ToListAsync();
    }
}
