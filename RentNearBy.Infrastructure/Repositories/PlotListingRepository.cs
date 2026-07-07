using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PlotListingRepository(ApplicationDbContext context) : Repository<PlotListing>(context), IPlotListingRoomListingRepository
{
    private record BoxQueryResult(
        Guid Id, double Lat, double Lng,
        double AreaValue, string AreaUnit, string PlotType,
        string? OwnerName, string? OwnerPhone, string? ThumbnailUrl);

    private static (double MinLat, double MaxLat, double MinLng, double MaxLng)
        GetBoundingBox(double lat, double lng, double radiusKm)
    {
        const double R = 6371.0;
        var dLat = radiusKm / R * (180.0 / Math.PI);
        var dLng = radiusKm / (R * Math.Cos(lat * Math.PI / 180.0)) * (180.0 / Math.PI);
        return (lat - dLat, lat + dLat, lng - dLng, lng + dLng);
    }

    public async Task<IEnumerable<NearbyPlotListingDto>> GetNearbyAsync(
        double latitude, double longitude, double radiusKm, Guid districtId)
    {
        var (minLat, maxLat, minLng, maxLng) = GetBoundingBox(latitude, longitude, radiusKm);

        // Scoped to the current District (a hard boundary); City is display-only and never gates visibility
        var box = await context.Database
            .SqlQuery<BoxQueryResult>($"""
                SELECT
                    p."Id",
                    p."Latitude"::float8   AS "Lat",
                    p."Longitude"::float8  AS "Lng",
                    p."AreaValue"::float8  AS "AreaValue",
                    p."AreaUnit"           AS "AreaUnit",
                    pt."Name"              AS "PlotType",
                    u."Name"               AS "OwnerName",
                    u."PhoneNumber"        AS "OwnerPhone",
                    ph."PhotoUrl"          AS "ThumbnailUrl"
                FROM "PlotListings" p
                INNER JOIN "PlotTypes" pt ON pt."Id" = p."PlotTypeId"
                LEFT JOIN "Users" u ON u."Id" = p."UserId"
                LEFT JOIN LATERAL (
                    SELECT ph."PhotoUrl"
                    FROM "PlotPhotos" ph
                    WHERE ph."PlotId" = p."Id"
                    ORDER BY ph."PhotoOrder"
                    LIMIT 1
                ) ph ON TRUE
                WHERE p."IsActive" = TRUE
                  AND p."IsDeleted" = FALSE
                  AND p."DistrictId" = {districtId}
                  AND p."Location" && ST_MakeEnvelope({minLng}::float8, {minLat}::float8,
                                                       {maxLng}::float8, {maxLat}::float8, 4326)::geography
                """)
            .ToListAsync();

        if (box.Count == 0)
            return Enumerable.Empty<NearbyPlotListingDto>();

        return box
            .Select(r => (Row: r, Dist: Haversine(latitude, longitude, r.Lat, r.Lng)))
            .Where(x => x.Dist <= radiusKm)
            .OrderBy(x => x.Dist)
            .Select(x => new NearbyPlotListingDto
            {
                Id = x.Row.Id,
                Latitude = (decimal)x.Row.Lat,
                Longitude = (decimal)x.Row.Lng,
                AreaValue = (decimal)x.Row.AreaValue,
                AreaUnit = x.Row.AreaUnit,
                PlotType = x.Row.PlotType,
                IsActive = true,
                OwnerName = x.Row.OwnerName,
                OwnerPhone = x.Row.OwnerPhone,
                ThumbnailUrl = x.Row.ThumbnailUrl,
                DistanceKm = x.Dist
            })
            .ToList();
    }

    public async Task<IEnumerable<PlotListing>> GetByUserIdAsync(Guid userId)
        => await _dbSet
            .AsNoTracking()
            .Include(p => p.PlotType)
            .Include(p => p.District)
            .Include(p => p.City)
            .Include(p => p.Photos.OrderBy(ph => ph.PhotoOrder).Take(1))
            .Where(p => p.UserId == userId && !p.IsDeleted)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

    public async Task<(IReadOnlyList<PlotListing> Items, bool HasMore)> GetByUserIdPagedAsync(
        Guid userId, int page, int pageSize)
    {
        var take = pageSize + 1;
        var items = await _dbSet
            .AsNoTracking()
            .Include(p => p.PlotType)
            .Include(p => p.District)
            .Include(p => p.City)
            .Include(p => p.Photos.OrderBy(ph => ph.PhotoOrder).Take(1))
            .Where(p => p.UserId == userId && !p.IsDeleted)
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(take)
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<PlotListing?> GetByIdWithPhotosAsync(Guid id)
        => await _dbSet
            .AsNoTracking()
            .Include(p => p.PlotType)
            .Include(p => p.District)
            .Include(p => p.City)
            .Include(p => p.User)
            .Include(p => p.Photos.OrderBy(ph => ph.PhotoOrder))
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);

    // Admin moderation needs to review reported plots even after the owner
    // deletes them — photos are gone for good (deleted from storage on delete),
    // but the listing record itself must still be visible.
    public async Task<PlotListing?> GetByIdWithPhotosForAdminAsync(Guid id)
        => await _dbSet
            .AsNoTracking()
            .Include(p => p.PlotType)
            .Include(p => p.District)
            .Include(p => p.City)
            .Include(p => p.User)
            .Include(p => p.Photos.OrderBy(ph => ph.PhotoOrder))
            .FirstOrDefaultAsync(p => p.Id == id);

    public async Task<(IReadOnlyList<PlotListing> Items, bool HasMore)> GetAllAsync(
        int page, int pageSize,
        string? plotType = null,
        bool? isActive = null,
        Guid? districtId = null,
        Guid? cityId = null)
    {
        var take = pageSize + 1;
        var query = _dbSet
            .AsNoTracking()
            .Include(p => p.PlotType)
            .Include(p => p.District)
            .Include(p => p.City)
            .Include(p => p.User)
            .Include(p => p.Photos.OrderBy(ph => ph.PhotoOrder).Take(1))
            .Where(p => !p.IsDeleted);

        if (plotType != null) query = query.Where(p => p.PlotType.Name == plotType);
        if (isActive.HasValue) query = query.Where(p => p.IsActive == isActive.Value);
        if (districtId.HasValue) query = query.Where(p => p.DistrictId == districtId.Value);
        if (cityId.HasValue) query = query.Where(p => p.CityId == cityId.Value);

        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(take)
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<IEnumerable<PlotListing>> GetActiveByUserIdAsync(Guid userId)
        => await _dbSet
            .Where(p => p.UserId == userId && p.IsActive && !p.IsDeleted)
            .ToListAsync();

    public async Task AddPhotoAsync(PlotPhoto photo)
        => await context.PlotPhotos.AddAsync(photo);

    public void RemovePhoto(PlotPhoto photo)
        => context.Entry(photo).State = Microsoft.EntityFrameworkCore.EntityState.Deleted;

    public async Task<IReadOnlyList<PendingDigestListingDto>> GetPendingDigestListingsAsync()
        => await _dbSet
            .AsNoTracking()
            .Where(p => p.IsActive && !p.IsDeleted && p.DigestNotifiedAt == null && p.District.IsActive)
            .Select(p => new PendingDigestListingDto
            {
                Id = p.Id,
                DistrictId = p.DistrictId,
                DistrictName = p.District.Name
            })
            .ToListAsync();

    public async Task<int> MarkDigestNotifiedAsync(IEnumerable<Guid> ids)
    {
        var idList = ids as IReadOnlyCollection<Guid> ?? ids.ToList();
        if (idList.Count == 0) return 0;

        return await _dbSet
            .Where(p => idList.Contains(p.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DigestNotifiedAt, DateTime.UtcNow));
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
