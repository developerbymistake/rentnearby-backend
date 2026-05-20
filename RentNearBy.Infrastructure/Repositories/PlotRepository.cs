using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PlotRepository(ApplicationDbContext context) : Repository<Plot>(context), IPlotRepository
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

    public async Task<IEnumerable<NearbyPlotDto>> GetNearbyAsync(
        double latitude, double longitude, double radiusKm, Guid cityId)
    {
        var (minLat, maxLat, minLng, maxLng) = GetBoundingBox(latitude, longitude, radiusKm);

        var box = await context.Database
            .SqlQuery<BoxQueryResult>($"""
                SELECT
                    p."Id",
                    p."Latitude"::float8   AS "Lat",
                    p."Longitude"::float8  AS "Lng",
                    p."AreaValue"::float8  AS "AreaValue",
                    p."AreaUnit"           AS "AreaUnit",
                    p."PlotType"           AS "PlotType",
                    u."Name"               AS "OwnerName",
                    u."PhoneNumber"        AS "OwnerPhone",
                    ph."PhotoUrl"          AS "ThumbnailUrl"
                FROM "Plots" p
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
                  AND p."CityId" = {cityId}
                  AND p."Location" && ST_MakeEnvelope({minLng}::float8, {minLat}::float8,
                                                       {maxLng}::float8, {maxLat}::float8, 4326)::geography
                """)
            .ToListAsync();

        if (box.Count == 0)
            return Enumerable.Empty<NearbyPlotDto>();

        return box
            .Select(r => (Row: r, Dist: Haversine(latitude, longitude, r.Lat, r.Lng)))
            .Where(x => x.Dist <= radiusKm)
            .OrderBy(x => x.Dist)
            .Select(x => new NearbyPlotDto
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

    public async Task<IEnumerable<Plot>> GetByUserIdAsync(Guid userId)
        => await _dbSet
            .AsNoTracking()
            .Include(p => p.District)
            .Include(p => p.City)
            .Include(p => p.Photos.OrderBy(ph => ph.PhotoOrder).Take(1))
            .Where(p => p.UserId == userId && !p.IsDeleted)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

    public async Task<(IReadOnlyList<Plot> Items, bool HasMore)> GetByUserIdPagedAsync(
        Guid userId, int page, int pageSize)
    {
        var take = pageSize + 1;
        var items = await _dbSet
            .AsNoTracking()
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

    public async Task<Plot?> GetByIdWithPhotosAsync(Guid id)
        => await _dbSet
            .AsNoTracking()
            .Include(p => p.District)
            .Include(p => p.City)
            .Include(p => p.User)
            .Include(p => p.Photos.OrderBy(ph => ph.PhotoOrder))
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);

    public async Task<(IReadOnlyList<Plot> Items, bool HasMore)> GetAllAsync(
        int page, int pageSize,
        string? plotType = null,
        bool? isActive = null,
        Guid? districtId = null,
        Guid? cityId = null)
    {
        var take = pageSize + 1;
        var query = _dbSet
            .AsNoTracking()
            .Include(p => p.District)
            .Include(p => p.City)
            .Include(p => p.User)
            .Include(p => p.Photos.OrderBy(ph => ph.PhotoOrder).Take(1))
            .Where(p => !p.IsDeleted);

        if (plotType != null) query = query.Where(p => p.PlotType == plotType);
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

    public async Task<IEnumerable<Plot>> GetActiveByUserIdAsync(Guid userId)
        => await _dbSet
            .Where(p => p.UserId == userId && p.IsActive && !p.IsDeleted)
            .ToListAsync();

    public async Task AddPhotoAsync(PlotPhoto photo)
        => await context.PlotPhotos.AddAsync(photo);

    public void RemovePhoto(PlotPhoto photo)
        => context.Entry(photo).State = Microsoft.EntityFrameworkCore.EntityState.Deleted;

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
