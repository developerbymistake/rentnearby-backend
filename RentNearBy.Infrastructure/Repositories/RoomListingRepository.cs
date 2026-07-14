using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class RoomListingRepository(ApplicationDbContext context) : Repository<RoomListing>(context), IRoomRoomListingRepository
{
    private record BoxQueryResult(
        Guid Id, int? PriceMonthly, double Lat, double Lng,
        string? RoomTypeName, string? OwnerName, string? OwnerPhone, string? ThumbnailUrl,
        string? FurnishedStatus);

    private static (double MinLat, double MaxLat, double MinLng, double MaxLng)
        GetBoundingBox(double lat, double lng, double radiusKm)
    {
        const double R = 6371.0;
        var dLat = radiusKm / R * (180.0 / Math.PI);
        var dLng = radiusKm / (R * Math.Cos(lat * Math.PI / 180.0)) * (180.0 / Math.PI);
        return (lat - dLat, lat + dLat, lng - dLng, lng + dLng);
    }

    public async Task<IEnumerable<NearbyListingDto>> GetNearbyAsync(
        double latitude, double longitude, double radiusKm, Guid districtId)
    {
        var (minLat, maxLat, minLng, maxLng) = GetBoundingBox(latitude, longitude, radiusKm);

        // Single DB query: GiST && bounding box + LATERAL JOIN for photos (N+1 fix)
        // isActive flag alone controls visibility — MembershipExpiryService deactivates expired listings
        // Scoped to the current District (a hard boundary); City is display-only and never gates visibility
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
                    p."PhotoUrl"   AS "ThumbnailUrl",
                    l."FurnishedStatus"
                FROM "RoomListings" l
                LEFT JOIN "RoomTypes" rt ON rt."Id" = l."RoomTypeId"
                LEFT JOIN "Users" u      ON u."Id"  = l."UserId"
                LEFT JOIN LATERAL (
                    SELECT p."PhotoUrl"
                    FROM "RoomPhotos" p
                    WHERE p."RoomListingId" = l."Id"
                    ORDER BY p."PhotoOrder"
                    LIMIT 1
                ) p ON TRUE
                WHERE l."IsActive" = TRUE
                  AND l."IsDeleted" = FALSE
                  AND l."DistrictId" = {districtId}
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
                DistanceKm = x.Dist,
                FurnishedStatus = x.Row.FurnishedStatus ?? "None"
            })
            .ToList();
    }

    public async Task<IEnumerable<RoomListing>> SearchAsync(Guid? districtId, Guid? roomTypeId, int? priceMin, int? priceMax, int? limit = null)
    {
        var query = _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l =>
                l.IsActive &&
                !l.IsDeleted &&
                (districtId == null || l.DistrictId == districtId) &&
                (roomTypeId == null || l.RoomTypeId == roomTypeId) &&
                (priceMin == null || l.PriceMonthly >= priceMin) &&
                (priceMax == null || l.PriceMonthly <= priceMax))
            .OrderByDescending(l => l.CreatedAt)
            .AsQueryable();

        if (limit.HasValue)
            query = query.Take(limit.Value);

        return await query.ToListAsync();
    }

    public async Task<(IReadOnlyList<RoomListing> Items, bool HasMore)> SearchPagedAsync(
        Guid? districtId, Guid? roomTypeId, string sortBy, int page, int pageSize)
    {
        var query = _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l =>
                l.IsActive &&
                !l.IsDeleted &&
                (districtId == null || l.DistrictId == districtId) &&
                (roomTypeId == null || l.RoomTypeId == roomTypeId));

        query = sortBy switch
        {
            "price_asc" => query.OrderBy(l => l.PriceMonthly),
            "price_desc" => query.OrderByDescending(l => l.PriceMonthly),
            _ => query.OrderByDescending(l => l.CreatedAt),
        };

        var take = pageSize + 1;
        var items = await query.Skip((page - 1) * pageSize).Take(take).ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<IEnumerable<RoomListing>> GetByUserIdAsync(Guid userId)
        => await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l => l.UserId == userId && !l.IsDeleted)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

    public async Task<IEnumerable<RoomListing>> GetActiveByUserIdAsync(Guid userId)
        => await _dbSet
            .Include(l => l.RoomType)
            .Include(l => l.City)
            .Where(l => l.UserId == userId && l.IsActive)
            .ToListAsync();

    public async Task<(IReadOnlyList<RoomListing> Items, bool HasMore)> GetByUserIdPagedAsync(
        Guid userId, int page, int pageSize)
    {
        var take = pageSize + 1;
        var items = await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l => l.UserId == userId && !l.IsDeleted)
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(take)
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<RoomListing?> GetByIdWithPhotosAsync(Guid id)
        => await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder))
            .FirstOrDefaultAsync(l => l.Id == id && !l.IsDeleted);

    // Admin moderation needs to review reported listings even after the owner
    // deletes them — photos are gone for good (deleted from storage on delete),
    // but the listing record itself must still be visible.
    public async Task<RoomListing?> GetByIdWithPhotosForAdminAsync(Guid id)
        => await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder))
            .FirstOrDefaultAsync(l => l.Id == id);

    public async Task AddPhotoAsync(RoomPhoto photo)
        => await context.RoomPhotos.AddAsync(photo);

    public void RemovePhoto(RoomPhoto photo)
    {
        context.Entry(photo).State = Microsoft.EntityFrameworkCore.EntityState.Deleted;
    }

    public async Task<IReadOnlyList<PendingDigestListingDto>> GetPendingDigestListingsAsync()
        => await _dbSet
            .AsNoTracking()
            .Where(l => l.IsActive && !l.IsDeleted && l.DigestNotifiedAt == null && l.District.IsActive)
            .Select(l => new PendingDigestListingDto
            {
                Id = l.Id,
                DistrictId = l.DistrictId,
                DistrictName = l.District.Name
            })
            .ToListAsync();

    public async Task<int> MarkDigestNotifiedAsync(IEnumerable<Guid> ids)
    {
        var idList = ids as IReadOnlyCollection<Guid> ?? ids.ToList();
        if (idList.Count == 0) return 0;

        return await _dbSet
            .Where(l => idList.Contains(l.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.DigestNotifiedAt, DateTime.UtcNow));
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
