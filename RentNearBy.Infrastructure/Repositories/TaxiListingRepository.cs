using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

// Public = Status Approved AND IsAvailable AND not deleted (literal in raw SQL so the planner can
// match the ix_taxilistings_browse_* / ix_taxilistings_public_id partial filters).
public class TaxiListingRepository(ApplicationDbContext context) : Repository<TaxiListing>(context), ITaxiListingRepository
{
    public async Task<TaxiListing?> GetLiveByUserAsync(Guid userId)
        => await _dbSet
            .Include(t => t.VehicleType)
            .Include(t => t.District)
            .Include(t => t.City)
            .Include(t => t.Routes.OrderBy(r => r.RouteIndex))
            .FirstOrDefaultAsync(t => t.UserId == userId && !t.IsDeleted);

    public async Task<string?> GetStatusByUserAsync(Guid userId)
        => await _dbSet
            .AsNoTracking()
            .Where(t => t.UserId == userId && !t.IsDeleted)
            .Select(t => t.Status)
            .FirstOrDefaultAsync();

    public async Task<bool> HasEverApprovedAsync(Guid userId)
        => await _dbSet.AnyAsync(t => t.UserId == userId && t.EverApproved);

    public async Task<bool> HasSubmittedHistoryAsync(Guid userId)
        => await _dbSet.AnyAsync(t => t.UserId == userId
            && (t.Status != TaxiStatuses.Draft || t.SubmittedAt != null || t.EverApproved));

    // Row-locks every row of the user and bumps xmin, so a concurrent tracked write fails as a concurrency conflict.
    public async Task<int> TouchAllByUserAsync(Guid userId)
        => await _dbSet
            .Where(t => t.UserId == userId)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.UpdatedAt, DateTime.UtcNow));

    public async Task<TaxiListing?> GetByIdWithAllAsync(Guid id)
        => await _dbSet
            .AsNoTracking()
            .Include(t => t.User)
            .Include(t => t.VehicleType)
            .Include(t => t.District)
            .Include(t => t.City)
            .Include(t => t.Routes.OrderBy(r => r.RouteIndex))
            .FirstOrDefaultAsync(t => t.Id == id && !t.IsDeleted);

    public async Task<TaxiListing?> GetBySlugWithAllAsync(string slug)
        => await _dbSet
            .AsNoTracking()
            .Include(t => t.User)
            .Include(t => t.VehicleType)
            .Include(t => t.District)
            .Include(t => t.City)
            .Include(t => t.Routes.OrderBy(r => r.RouteIndex))
            .FirstOrDefaultAsync(t => t.Slug == slug && !t.IsDeleted && t.Status == TaxiStatuses.Approved && t.IsAvailable);

    public async Task<TaxiListing?> GetByIdForAdminAsync(Guid id)
        => await _dbSet
            .AsNoTracking()
            .Include(t => t.User)
            .Include(t => t.VehicleType)
            .Include(t => t.District)
            .Include(t => t.City)
            .Include(t => t.Routes.OrderBy(r => r.RouteIndex))
            .FirstOrDefaultAsync(t => t.Id == id);

    public async Task<bool> IsPlateTakenAsync(string numberPlate, Guid exceptUserId)
        => await _dbSet
            .AsNoTracking()
            .AnyAsync(t => t.NumberPlate == numberPlate && !t.IsDeleted && t.UserId != exceptUserId);

    private sealed record LocalRow(
        Guid Id, string? DriverName, string? VehicleTypeName, string? VehicleCategory, short? Seats,
        string? NumberPlate, string? CityName, string? DistrictName, int? MinFare, int RouteCount, DateTime CreatedAt);

    public async Task<(IReadOnlyList<TaxiBrowseDto> Items, bool HasMore)> BrowseLocalPagedAsync(
        Guid districtId, Guid? cityId, string? category, int page, int pageSize)
    {
        var query = _dbSet
            .AsNoTracking()
            .Where(t => t.Status == TaxiStatuses.Approved && t.IsAvailable && !t.IsDeleted && t.DistrictId == districtId);
        if (category != null)
            query = query.Where(t => t.VehicleType != null && t.VehicleType.Category == category);

        var rows = await query
            .OrderBy(t => cityId != null && t.CityId == cityId ? 0 : 1)
            .ThenBy(t => t.MinFare)
            .ThenBy(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .Select(t => new LocalRow(
                t.Id,
                t.User.Name,
                t.VehicleType != null ? t.VehicleType.Name : null,
                t.VehicleType != null ? t.VehicleType.Category : null,
                t.Seats,
                t.NumberPlate,
                t.City != null ? t.City.Name : null,
                t.District != null ? t.District.Name : null,
                t.MinFare,
                t.Routes.Count,
                t.CreatedAt))
            .ToListAsync();

        var hasMore = rows.Count > pageSize;
        var items = rows.Take(pageSize).Select(r => new TaxiBrowseDto
        {
            Id = r.Id,
            DriverName = r.DriverName,
            VehicleTypeName = r.VehicleTypeName,
            VehicleCategory = r.VehicleCategory,
            Seats = r.Seats,
            PlateDisplay = TaxiPlate.Format(r.NumberPlate),
            CityName = r.CityName,
            DistrictName = r.DistrictName,
            MinFare = r.MinFare,
            RouteCount = r.RouteCount,
            CreatedAt = r.CreatedAt,
        }).ToList();
        return (items, hasMore);
    }

    private sealed record SearchRow(
        Guid Id, string? DriverName, string? VehicleTypeName, string? VehicleCategory, short? Seats,
        string? NumberPlate, string? CityName, string? DistrictName, int? MinFare, DateTime CreatedAt,
        long RouteCount, int MatchedFare, string MatchedFromName, string MatchedToName, string MatchedTripType,
        bool MatchReversed);

    // Route match runs first over TaxiRoutes (the two GiST indexes drive the ST_DWithin pairs), one
    // row per taxi via DISTINCT ON (direct before reverse, then cheapest), then the public filter and
    // paging. A reversed match swaps the stored names so they follow the traveller's direction.
    public async Task<(IReadOnlyList<TaxiBrowseDto> Items, bool HasMore)> SearchByRoutePagedAsync(
        double fromLat, double fromLng, double toLat, double toLng, string? category, int page, int pageSize)
    {
        var radius = TaxiLimits.MatchRadiusMeters;
        var offset = (page - 1) * pageSize;
        var take = pageSize + 1;

        var rows = await context.Database
            .SqlQuery<SearchRow>($"""
                WITH q AS (
                    SELECT ST_MakePoint({fromLng}::float8, {fromLat}::float8)::geography AS f,
                           ST_MakePoint({toLng}::float8, {toLat}::float8)::geography     AS t
                ),
                matched AS (
                    SELECT r."TaxiListingId", r."Fare", r."TripType",
                           CASE WHEN m.direct THEN r."FromName" ELSE r."ToName"   END AS "FromName",
                           CASE WHEN m.direct THEN r."ToName"   ELSE r."FromName" END AS "ToName",
                           NOT m.direct AS "Reversed"
                    FROM "TaxiRoutes" r
                    CROSS JOIN q
                    CROSS JOIN LATERAL (
                        SELECT (ST_DWithin(r."FromPoint", q.f, {radius}::float8) AND ST_DWithin(r."ToPoint", q.t, {radius}::float8)) AS direct
                    ) m
                    WHERE (ST_DWithin(r."FromPoint", q.f, {radius}::float8) AND ST_DWithin(r."ToPoint", q.t, {radius}::float8))
                       OR (ST_DWithin(r."FromPoint", q.t, {radius}::float8) AND ST_DWithin(r."ToPoint", q.f, {radius}::float8))
                ),
                best AS (
                    SELECT DISTINCT ON ("TaxiListingId") *
                    FROM matched
                    ORDER BY "TaxiListingId", "Reversed", "Fare"
                )
                SELECT t."Id",
                       u."Name"      AS "DriverName",
                       v."Name"      AS "VehicleTypeName",
                       v."Category"  AS "VehicleCategory",
                       t."Seats",
                       t."NumberPlate",
                       c."Name"      AS "CityName",
                       d."Name"      AS "DistrictName",
                       t."MinFare",
                       t."CreatedAt",
                       (SELECT COUNT(*) FROM "TaxiRoutes" rc WHERE rc."TaxiListingId" = t."Id") AS "RouteCount",
                       b."Fare"      AS "MatchedFare",
                       b."FromName"  AS "MatchedFromName",
                       b."ToName"    AS "MatchedToName",
                       b."TripType"  AS "MatchedTripType",
                       b."Reversed"  AS "MatchReversed"
                FROM best b
                JOIN "TaxiListings" t ON t."Id" = b."TaxiListingId"
                  AND t."Status" = 'Approved' AND t."IsAvailable" = TRUE AND t."IsDeleted" = FALSE
                JOIN "Users" u ON u."Id" = t."UserId"
                JOIN "TaxiVehicleTypes" v ON v."Id" = t."VehicleTypeId"
                LEFT JOIN "Cities" c ON c."Id" = t."CityId"
                LEFT JOIN "Districts" d ON d."Id" = t."DistrictId"
                WHERE ({category}::text IS NULL OR v."Category" = {category}::text)
                ORDER BY b."Reversed", b."Fare", t."Id"
                OFFSET {offset} LIMIT {take}
                """)
            .ToListAsync();

        var hasMore = rows.Count > pageSize;
        var items = rows.Take(pageSize).Select(r => new TaxiBrowseDto
        {
            Id = r.Id,
            DriverName = r.DriverName,
            VehicleTypeName = r.VehicleTypeName,
            VehicleCategory = r.VehicleCategory,
            Seats = r.Seats,
            PlateDisplay = TaxiPlate.Format(r.NumberPlate),
            CityName = r.CityName,
            DistrictName = r.DistrictName,
            MinFare = r.MinFare,
            RouteCount = (int)r.RouteCount,
            MatchedFare = r.MatchedFare,
            MatchedFromName = r.MatchedFromName,
            MatchedToName = r.MatchedToName,
            MatchedTripType = r.MatchedTripType,
            MatchReversed = r.MatchReversed,
            CreatedAt = r.CreatedAt,
        }).ToList();
        return (items, hasMore);
    }

    public async Task<(IReadOnlyList<TaxiListing> Items, bool HasMore)> GetAdminPagedAsync(
        string status, int page, int pageSize)
    {
        var query = _dbSet
            .AsNoTracking()
            .Include(t => t.User)
            .Include(t => t.VehicleType)
            .Include(t => t.District)
            .Include(t => t.City)
            .Include(t => t.Routes.OrderBy(r => r.RouteIndex))
            .Where(t => !t.IsDeleted);
        if (status != "All")
            query = query.Where(t => t.Status == status);

        var items = await query
            .OrderByDescending(t => t.SubmittedAt != null)
            .ThenByDescending(t => t.SubmittedAt)
            .ThenByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }
}
