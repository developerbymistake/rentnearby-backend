using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class ListingRepository(ApplicationDbContext context) : Repository<Listing>(context), IListingRepository
{
    private record CandidateDistance(Guid Id, double DistanceKm);

    public async Task<IEnumerable<NearbyListingResult>> GetNearbyAsync(
        double latitude, double longitude, double radiusKm, Guid cityId)
    {
        double radiusMeters = radiusKm * 1000.0;

        // Step 1: PostGIS ST_DWithin for exact geographic circle filter.
        // Returns only listings within the radius and their DB-computed distances.
        // Uses ix_listings_location_gist (geography GIST index).
        var candidates = await context.Database
            .SqlQuery<CandidateDistance>($"""
                SELECT l."Id",
                    ST_Distance(
                        ST_MakePoint(l."Longitude"::float8, l."Latitude"::float8)::geography,
                        ST_MakePoint({longitude}::float8, {latitude}::float8)::geography
                    ) / 1000.0 AS "DistanceKm"
                FROM "Listings" l
                WHERE l."IsActive" = TRUE
                  AND l."CityId" = {cityId}
                  AND ST_DWithin(
                        ST_MakePoint(l."Longitude"::float8, l."Latitude"::float8)::geography,
                        ST_MakePoint({longitude}::float8, {latitude}::float8)::geography,
                        {radiusMeters}
                      )
                """)
            .ToListAsync();

        if (candidates.Count == 0)
            return Enumerable.Empty<NearbyListingResult>();

        var ids = candidates.Select(c => c.Id).ToList();
        var distanceMap = candidates.ToDictionary(c => c.Id, c => c.DistanceKm);

        // Step 2: Load full entities with navigation properties for the candidate set.
        var listings = await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l => ids.Contains(l.Id))
            .ToListAsync();

        return listings
            .Select(l => new NearbyListingResult(l, distanceMap[l.Id]))
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
