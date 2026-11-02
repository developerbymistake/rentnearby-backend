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
}
