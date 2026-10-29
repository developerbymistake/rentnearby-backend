using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

// Live to guests = Status Approved AND AcceptingGuests AND not deleted (literal in raw SQL so the
// planner can match the ix_staylistings_browse_* partial filters).
public class StayListingRepository(ApplicationDbContext context) : Repository<StayListing>(context), IStayListingRepository
{
    public async Task<StayListing?> GetLiveByUserAsync(Guid userId)
        => await _dbSet
            .Include(s => s.StayType)
            .Include(s => s.District)
            .Include(s => s.City)
            .Include(s => s.Rooms.OrderBy(r => r.RoomIndex))
            .Include(s => s.Photos.OrderBy(p => p.PhotoOrder))
            .FirstOrDefaultAsync(s => s.UserId == userId && !s.IsDeleted);

    public async Task<string?> GetStatusByUserAsync(Guid userId)
        => await _dbSet
            .AsNoTracking()
            .Where(s => s.UserId == userId && !s.IsDeleted)
            .Select(s => s.Status)
            .FirstOrDefaultAsync();

    public async Task<bool> HasEverApprovedAsync(Guid userId)
        => await _dbSet.AnyAsync(s => s.UserId == userId && s.EverApproved);

    public async Task<bool> HasSubmittedHistoryAsync(Guid userId)
        => await _dbSet.AnyAsync(s => s.UserId == userId
            && (s.Status != StayStatuses.Draft || s.SubmittedAt != null || s.EverApproved));

    // Row-locks every row of the user and bumps xmin, so a concurrent tracked write fails as a concurrency conflict.
    public async Task<int> TouchAllByUserAsync(Guid userId)
        => await _dbSet
            .Where(s => s.UserId == userId)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.UpdatedAt, DateTime.UtcNow));

    public async Task<StayListing?> GetByIdWithAllAsync(Guid id)
        => await _dbSet
            .AsNoTracking()
            .Include(s => s.User)
            .Include(s => s.StayType)
            .Include(s => s.District)
            .Include(s => s.City)
            .Include(s => s.Rooms.OrderBy(r => r.RoomIndex))
            .Include(s => s.Photos.OrderBy(p => p.PhotoOrder))
            .FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted);

    // Live only: the slug exists from the first step-1 save, so drafts, stays under review and
    // paused or suspended stays must not be reachable through it.
    public async Task<StayListing?> GetBySlugWithAllAsync(string slug)
        => await _dbSet
            .AsNoTracking()
            .Include(s => s.StayType)
            .Include(s => s.District)
            .Include(s => s.City)
            .Include(s => s.Rooms.OrderBy(r => r.RoomIndex))
            .Include(s => s.Photos.OrderBy(p => p.PhotoOrder))
            .FirstOrDefaultAsync(s => s.Slug == slug && !s.IsDeleted && s.Status == StayStatuses.Approved && s.AcceptingGuests);

    public async Task<StayListing?> GetByIdForAdminAsync(Guid id)
        => await _dbSet
            .AsNoTracking()
            .Include(s => s.User)
            .Include(s => s.StayType)
            .Include(s => s.District)
            .Include(s => s.City)
            .Include(s => s.Rooms.OrderBy(r => r.RoomIndex))
            .Include(s => s.Photos.OrderBy(p => p.PhotoOrder))
            .FirstOrDefaultAsync(s => s.Id == id);

    private static readonly Expression<Func<StayListing, StayBrowseDto>> BrowseProjection = s => new StayBrowseDto
    {
        Id = s.Id,
        UserId = s.UserId,
        Name = s.Name,
        StayTypeId = s.StayTypeId,
        StayTypeName = s.StayType != null ? s.StayType.Name : null,
        ThumbnailUrl = s.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault(),
        CityName = s.City != null ? s.City.Name : null,
        DistrictName = s.District != null ? s.District.Name : null,
        MinPricePerNight = s.MinPricePerNight,
        MealsChip = s.Rooms.Any(r => r.Meals == StayMeals.AllMeals) ? "All meals"
            : s.Rooms.Any(r => r.Meals == StayMeals.Breakfast) ? "Breakfast"
            : null,
        RoomNameChip = s.Rooms.OrderBy(r => r.PricePerNight).Select(r => r.RoomName).FirstOrDefault(),
        CreatedAt = s.CreatedAt,
    };

    // District-free Home "Recently added" (ix_staylistings_recent_active).
    public async Task<List<StayBrowseDto>> GetRecentProjectedAsync(int limit)
        => await _dbSet.AsNoTracking()
            .Where(s => s.Status == StayStatuses.Approved && s.AcceptingGuests && !s.IsDeleted)
            .OrderByDescending(s => s.CreatedAt)
            .Take(limit)
            .Select(BrowseProjection)
            .ToListAsync();

    public async Task<BrowsePage<StayBrowseDto>> BrowsePagedAsync(
        Guid districtId, Guid? cityId, string? meals, Guid? stayTypeId, string sortBy, int page, int pageSize, string? cursor)
    {
        page = Math.Clamp(page, 1, BrowseCursor.MaxPage);

        var query = _dbSet
            .AsNoTracking()
            .Where(s => s.Status == StayStatuses.Approved && s.AcceptingGuests && !s.IsDeleted
                && s.DistrictId == districtId);
        if (meals != null && StayMeals.All.Contains(meals))
            query = query.Where(s => s.Rooms.Any(r => r.Meals == meals));
        if (stayTypeId.HasValue)
            query = query.Where(s => s.StayTypeId == stayTypeId);

        if (sortBy is "price_asc" or "price_desc")
        {
            var cityFirst = query.OrderBy(s => cityId != null && s.CityId == cityId ? 0 : 1);
            var byPrice = sortBy == "price_asc"
                ? cityFirst.ThenBy(s => s.MinPricePerNight).ThenBy(s => s.Id)
                : cityFirst.ThenByDescending(s => s.MinPricePerNight).ThenByDescending(s => s.Id);
            var priced = await byPrice.Skip((page - 1) * pageSize).Take(pageSize + 1).Select(BrowseProjection).ToListAsync();
            return BrowseCursor.OffsetPage(priced, pageSize);
        }

        if (string.IsNullOrEmpty(cursor) && page > 1)
        {
            var offset = await query
                .OrderBy(s => cityId != null && s.CityId == cityId ? 0 : 1)
                .ThenByDescending(s => s.CreatedAt)
                .ThenByDescending(s => s.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize + 1)
                .Select(BrowseProjection)
                .ToListAsync();
            return BrowseCursor.OffsetPage(offset, pageSize);
        }

        var phase = cityId.HasValue ? 0 : 1;
        DateTime? afterAt = null;
        var afterId = Guid.Empty;
        if (BrowseCursor.TryDecode(cursor, out var cursorPhase, out var cursorAt, out var cursorId) && cursorPhase >= phase)
        {
            phase = cursorPhase;
            afterAt = cursorAt;
            afterId = cursorId;
        }

        var items = new List<StayBrowseDto>(pageSize + 1);
        var phase0Count = 0;
        for (; phase <= 1; phase++)
        {
            var need = pageSize + 1 - items.Count;
            var phaseQuery = query;
            if (cityId.HasValue)
            {
                var cid = cityId.Value;
                phaseQuery = phase == 0
                    ? phaseQuery.Where(s => s.CityId == cid)
                    : phaseQuery.Where(s => s.CityId == null || s.CityId != cid);
            }
            if (afterAt.HasValue)
            {
                var at = afterAt.Value;
                var aid = afterId;
                phaseQuery = phaseQuery.Where(s => s.CreatedAt <= at && (s.CreatedAt < at || s.Id.CompareTo(aid) < 0));
            }

            var rows = await phaseQuery
                .OrderByDescending(s => s.CreatedAt)
                .ThenByDescending(s => s.Id)
                .Take(need)
                .Select(BrowseProjection)
                .ToListAsync();
            items.AddRange(rows);
            if (phase == 0) phase0Count = items.Count;
            afterAt = null;
            if (rows.Count == need) break;
        }

        return BrowseCursor.KeysetPage(items, pageSize, phase0Count, x => x.CreatedAt, x => x.Id);
    }

    private record BoxQueryResult(
        Guid Id, string? Name, Guid? StayTypeId, string? StayTypeName, int? MinPricePerNight, double Lat, double Lng,
        string? OwnerName, string? OwnerPhone, string? ThumbnailUrl);

    private static (double MinLat, double MaxLat, double MinLng, double MaxLng)
        GetBoundingBox(double lat, double lng, double radiusKm)
    {
        const double R = 6371.0;
        var dLat = radiusKm / R * (180.0 / Math.PI);
        var dLng = radiusKm / (R * Math.Cos(lat * Math.PI / 180.0)) * (180.0 / Math.PI);
        return (lat - dLat, lat + dLat, lng - dLng, lng + dLng);
    }

    public async Task<IEnumerable<StayNearbyDto>> GetNearbyAsync(
        double latitude, double longitude, double radiusKm, Guid districtId, Guid? stayTypeId)
    {
        var (minLat, maxLat, minLng, maxLng) = GetBoundingBox(latitude, longitude, radiusKm);

        var box = await context.Database
            .SqlQuery<BoxQueryResult>($"""
                SELECT
                    s."Id",
                    s."Name",
                    s."StayTypeId",
                    t."Name"       AS "StayTypeName",
                    s."MinPricePerNight",
                    s."Latitude"::float8   AS "Lat",
                    s."Longitude"::float8  AS "Lng",
                    u."Name"       AS "OwnerName",
                    CASE WHEN u."IsContactVisible" THEN u."PhoneNumber" END AS "OwnerPhone",
                    p."PhotoUrl"   AS "ThumbnailUrl"
                FROM "StayListings" s
                LEFT JOIN "Users" u ON u."Id" = s."UserId"
                LEFT JOIN "StayTypes" t ON t."Id" = s."StayTypeId"
                LEFT JOIN LATERAL (
                    SELECT p."PhotoUrl"
                    FROM "StayPhotos" p
                    WHERE p."StayListingId" = s."Id"
                    ORDER BY p."PhotoOrder"
                    LIMIT 1
                ) p ON TRUE
                WHERE s."Status" = 'Approved'
                  AND s."AcceptingGuests" = TRUE
                  AND s."IsDeleted" = FALSE
                  AND s."DistrictId" = {districtId}
                  AND ({stayTypeId}::uuid IS NULL OR s."StayTypeId" = {stayTypeId}::uuid)
                  AND s."Location" && ST_MakeEnvelope({minLng}::float8, {minLat}::float8,
                                                       {maxLng}::float8, {maxLat}::float8, 4326)::geography
                """)
            .ToListAsync();

        if (box.Count == 0)
            return Enumerable.Empty<StayNearbyDto>();

        return box
            .Select(r => (Row: r, Dist: Haversine(latitude, longitude, r.Lat, r.Lng)))
            .Where(x => x.Dist <= radiusKm)
            .OrderBy(x => x.Dist)
            .Select(x => new StayNearbyDto
            {
                Id = x.Row.Id,
                Name = x.Row.Name,
                StayTypeId = x.Row.StayTypeId,
                StayTypeName = x.Row.StayTypeName,
                MinPricePerNight = x.Row.MinPricePerNight,
                Latitude = (decimal)x.Row.Lat,
                Longitude = (decimal)x.Row.Lng,
                IsActive = true,
                OwnerName = x.Row.OwnerName,
                OwnerPhone = x.Row.OwnerPhone,
                ThumbnailUrl = x.Row.ThumbnailUrl,
                DistanceKm = x.Dist,
            })
            .ToList();
    }

    private record KnnQueryResult(
        Guid Id, string? Name, Guid? StayTypeId, string? StayTypeName, int? MinPricePerNight, double Lat, double Lng,
        string? OwnerName, string? OwnerPhone, string? ThumbnailUrl, double DistanceMeters);

    public async Task<IEnumerable<StayNearbyDto>> GetNearestAsync(
        double latitude, double longitude, int count, Guid districtId, Guid? stayTypeId)
    {
        // <-> is KNN-index-assisted by ix_staylistings_location_gist.
        var rows = await context.Database
            .SqlQuery<KnnQueryResult>($"""
                SELECT
                    s."Id",
                    s."Name",
                    s."StayTypeId",
                    t."Name"       AS "StayTypeName",
                    s."MinPricePerNight",
                    s."Latitude"::float8   AS "Lat",
                    s."Longitude"::float8  AS "Lng",
                    u."Name"       AS "OwnerName",
                    CASE WHEN u."IsContactVisible" THEN u."PhoneNumber" END AS "OwnerPhone",
                    p."PhotoUrl"   AS "ThumbnailUrl",
                    ST_Distance(s."Location", ST_MakePoint({longitude}, {latitude})::geography) AS "DistanceMeters"
                FROM "StayListings" s
                LEFT JOIN "Users" u ON u."Id" = s."UserId"
                LEFT JOIN "StayTypes" t ON t."Id" = s."StayTypeId"
                LEFT JOIN LATERAL (
                    SELECT p."PhotoUrl"
                    FROM "StayPhotos" p
                    WHERE p."StayListingId" = s."Id"
                    ORDER BY p."PhotoOrder"
                    LIMIT 1
                ) p ON TRUE
                WHERE s."Status" = 'Approved'
                  AND s."AcceptingGuests" = TRUE
                  AND s."IsDeleted" = FALSE
                  AND s."Location" IS NOT NULL
                  AND s."DistrictId" = {districtId}
                  AND ({stayTypeId}::uuid IS NULL OR s."StayTypeId" = {stayTypeId}::uuid)
                ORDER BY s."Location" <-> ST_MakePoint({longitude}, {latitude})::geography
                LIMIT {count}
                """)
            .ToListAsync();

        return rows
            .Select(r => new StayNearbyDto
            {
                Id = r.Id,
                Name = r.Name,
                StayTypeId = r.StayTypeId,
                StayTypeName = r.StayTypeName,
                MinPricePerNight = r.MinPricePerNight,
                Latitude = (decimal)r.Lat,
                Longitude = (decimal)r.Lng,
                IsActive = true,
                OwnerName = r.OwnerName,
                OwnerPhone = r.OwnerPhone,
                ThumbnailUrl = r.ThumbnailUrl,
                DistanceKm = r.DistanceMeters / 1000.0,
            })
            .ToList();
    }

    public async Task<(IReadOnlyList<StayListing> Items, bool HasMore)> GetAdminPagedAsync(
        string status, int page, int pageSize)
    {
        var take = pageSize + 1;
        var items = await _dbSet
            .AsNoTracking()
            .Include(s => s.User)
            .Include(s => s.StayType)
            .Include(s => s.District)
            .Include(s => s.City)
            .Include(s => s.Rooms.OrderBy(r => r.RoomIndex))
            .Where(s => !s.IsDeleted && s.Status == status)
            .OrderByDescending(s => s.SubmittedAt)
            .ThenByDescending(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(take)
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
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
