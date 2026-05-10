using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class ListingRepository(ApplicationDbContext context) : Repository<Listing>(context), IListingRepository
{
    private record BoxQueryResult(
        Guid Id, int? PriceMonthly, double Lat, double Lng,
        string? RoomTypeName, string? OwnerName, string? OwnerPhone, string? ThumbnailUrl);

    private static (double MinLat, double MaxLat, double MinLng, double MaxLng)
        GetBoundingBox(double lat, double lng, double radiusKm)
    {
        const double R = 6371.0;
        var dLat = radiusKm / R * (180.0 / Math.PI);
        var dLng = radiusKm / (R * Math.Cos(lat * Math.PI / 180.0)) * (180.0 / Math.PI);
        return (lat - dLat, lat + dLat, lng - dLng, lng + dLng);
    }

    public async Task<IEnumerable<NearbyListingDto>> GetNearbyAsync(
        double latitude, double longitude, double radiusKm, Guid cityId)
    {
        var (minLat, maxLat, minLng, maxLng) = GetBoundingBox(latitude, longitude, radiusKm);

        // Single DB query: GiST && bounding box + LATERAL JOIN for photos (N+1 fix)
        // LATERAL JOIN replaces correlated subquery - executes once instead of per-row
        // Index on ListingPhotos(ListingId, PhotoOrder) enables efficient first-photo lookup
        var box = await context.Database
            .SqlQuery<BoxQueryResult>($"""
                SELECT
                    l."Id",
                    l."PriceMonthly",
                    l."Latitude"::float8   AS "Lat",
                    l."Longitude"::float8  AS "Lng",
                    rt."Name"      AS "RoomTypeName",
                    u."Name"       AS "OwnerName",
                    u."PhoneNumber" AS "OwnerPhone",
                    p."PhotoUrl"   AS "ThumbnailUrl"
                FROM "Listings" l
                LEFT JOIN "RoomTypes" rt ON rt."Id" = l."RoomTypeId"
                LEFT JOIN "Users" u      ON u."Id"  = l."UserId"
                LEFT JOIN LATERAL (
                    SELECT p."PhotoUrl"
                    FROM "ListingPhotos" p
                    WHERE p."ListingId" = l."Id"
                    ORDER BY p."PhotoOrder"
                    LIMIT 1
                ) p ON TRUE
                WHERE l."IsActive" = TRUE
                  AND l."CityId" = {cityId}
                  AND l."Location" && ST_MakeEnvelope({minLng}::float8, {minLat}::float8,
                                                       {maxLng}::float8, {maxLat}::float8, 4326)::geography
                """)
            .ToListAsync();

        if (box.Count == 0)
            return Enumerable.Empty<NearbyListingDto>();

        return box
            .Select(r => (Row: r, Dist: Haversine(latitude, longitude, r.Lat, r.Lng)))
            .Where(x => x.Dist <= radiusKm)
            .OrderBy(x => x.Dist)
            .Select(x => new NearbyListingDto
            {
                Id = x.Row.Id,
                PriceMonthly = x.Row.PriceMonthly,
                Latitude = (decimal)x.Row.Lat,
                Longitude = (decimal)x.Row.Lng,
                IsActive = true,
                RoomTypeName = x.Row.RoomTypeName,
                OwnerName = x.Row.OwnerName,
                OwnerPhone = x.Row.OwnerPhone,
                ThumbnailUrl = x.Row.ThumbnailUrl,
                DistanceKm = x.Dist
            })
            .ToList();
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
