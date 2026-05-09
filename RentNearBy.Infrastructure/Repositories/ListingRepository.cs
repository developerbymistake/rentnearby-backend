using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class ListingRepository(ApplicationDbContext context) : Repository<Listing>(context), IListingRepository
{
    public async Task<IEnumerable<NearbyListingResult>> GetNearbyAsync(
        double latitude, double longitude, double radiusKm, Guid cityId)
    {
        double latDelta = radiusKm / 111.0;
        double lngDelta = radiusKm / (111.0 * Math.Cos(latitude * Math.PI / 180.0));
        double latMin = latitude - latDelta, latMax = latitude + latDelta;
        double lngMin = longitude - lngDelta, lngMax = longitude + lngDelta;

        // Step 1: GIST bounding-box pre-filter uses ix_listings_location_gist.
        // point("Longitude","Latitude") <@ box(...) is the only operator EF does
        // not translate, so raw SQL is required to activate the GIST index.
        var candidateIds = await _dbSet
            .FromSqlInterpolated($"""
                SELECT * FROM "Listings"
                WHERE "IsActive" = TRUE
                  AND "CityId" = {cityId}
                  AND point("Longitude", "Latitude") <@ box(point({lngMin},{latMin}),point({lngMax},{latMax}))
                """)
            .AsNoTracking()
            .Select(l => l.Id)
            .ToListAsync();

        if (candidateIds.Count == 0)
            return Enumerable.Empty<NearbyListingResult>();

        // Step 2: Load full entities with navigation properties for the candidate set.
        var listings = await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l => candidateIds.Contains(l.Id))
            .ToListAsync();

        return listings
            .Select(l => new NearbyListingResult(
                l, Haversine(latitude, longitude, (double)l.Latitude, (double)l.Longitude)))
            .Where(r => r.DistanceKm <= radiusKm)
            .OrderBy(r => r.DistanceKm);
    }

    public async Task<IEnumerable<Listing>> SearchAsync(Guid? districtId, Guid? roomTypeId, int? priceMin, int? priceMax)
        => await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l =>
                l.IsActive &&
                (districtId == null || l.DistrictId == districtId) &&
                (roomTypeId == null || l.RoomTypeId == roomTypeId) &&
                (priceMin == null || l.PriceMonthly >= priceMin) &&
                (priceMax == null || l.PriceMonthly <= priceMax))
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

    public async Task<IEnumerable<Listing>> GetByUserIdAsync(Guid userId)
        => await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

    public async Task<(IReadOnlyList<Listing> Items, bool HasMore)> GetByUserIdPagedAsync(
        Guid userId, int page, int pageSize)
    {
        var take = pageSize + 1;
        var items = await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(take)
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<Listing?> GetByIdWithPhotosAsync(Guid id)
        => await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder))
            .FirstOrDefaultAsync(l => l.Id == id);

    public async Task AddPhotoAsync(ListingPhoto photo)
        => await context.ListingPhotos.AddAsync(photo);

    public void RemovePhoto(ListingPhoto photo)
    {
        context.Entry(photo).State = Microsoft.EntityFrameworkCore.EntityState.Deleted;
    }

    private static double Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
