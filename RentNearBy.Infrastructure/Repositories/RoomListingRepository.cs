using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Infrastructure.Repositories;

public class RoomListingRepository(ApplicationDbContext context) : Repository<RoomListing>(context), IRoomRoomListingRepository
{
    private record BoxQueryResult(
        Guid Id, int? PriceMonthly, double Lat, double Lng,
        string? RoomTypeName, string? OwnerName, string? OwnerPhone, string? ThumbnailUrl,
        string? FurnishedStatus, bool? IsAc, short PriorityRank, bool IsFeatured);

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
                    CASE WHEN u."IsContactVisible" THEN u."PhoneNumber" END AS "OwnerPhone",
                    p."PhotoUrl"   AS "ThumbnailUrl",
                    l."FurnishedStatus",
                    l."IsAc",
                    l."PriorityRank",
                    l."IsFeatured"
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
            .OrderByDescending(x => x.Row.PriorityRank)
            .ThenBy(x => x.Dist)
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
                FurnishedStatus = x.Row.FurnishedStatus ?? "None",
                IsAc = x.Row.IsAc,
                PriorityRank = x.Row.PriorityRank,
                IsFeatured = x.Row.IsFeatured,
            })
            .ToList();
    }

    private record KnnQueryResult(
        Guid Id, int? PriceMonthly, double Lat, double Lng,
        string? RoomTypeName, string? OwnerName, string? OwnerPhone, string? ThumbnailUrl,
        string? FurnishedStatus, bool? IsAc, short PriorityRank, bool IsFeatured, double DistanceMeters);

    public async Task<IEnumerable<NearbyListingDto>> GetNearestAsync(
        double latitude, double longitude, int count, Guid districtId)
    {
        // <-> is KNN-index-assisted by ix_listings_location_gist — no bounding box needed. The n nearest
        // rows are then re-sorted in memory, paid rank first.
        var rows = await context.Database
            .SqlQuery<KnnQueryResult>($"""
                SELECT
                    l."Id",
                    l."PriceMonthly",
                    l."Latitude"::float8   AS "Lat",
                    l."Longitude"::float8  AS "Lng",
                    rt."Name"      AS "RoomTypeName",
                    u."Name"       AS "OwnerName",
                    CASE WHEN u."IsContactVisible" THEN u."PhoneNumber" END AS "OwnerPhone",
                    p."PhotoUrl"   AS "ThumbnailUrl",
                    l."FurnishedStatus",
                    l."IsAc",
                    l."PriorityRank",
                    l."IsFeatured",
                    ST_Distance(l."Location", ST_MakePoint({longitude}, {latitude})::geography) AS "DistanceMeters"
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
                ORDER BY l."Location" <-> ST_MakePoint({longitude}, {latitude})::geography
                LIMIT {count}
                """)
            .ToListAsync();

        return rows
            .OrderByDescending(r => r.PriorityRank)
            .ThenBy(r => r.DistanceMeters)
            .Select(r => new NearbyListingDto
            {
                Id = r.Id,
                PriceMonthly = r.PriceMonthly,
                Latitude = (decimal)r.Lat,
                Longitude = (decimal)r.Lng,
                IsActive = true,
                RoomTypeName = r.RoomTypeName,
                OwnerName = r.OwnerName,
                OwnerPhone = r.OwnerPhone,
                ThumbnailUrl = r.ThumbnailUrl,
                DistanceKm = r.DistanceMeters / 1000.0,
                FurnishedStatus = r.FurnishedStatus ?? "None",
                IsAc = r.IsAc,
                PriorityRank = r.PriorityRank,
                IsFeatured = r.IsFeatured,
            })
            .ToList();
    }

    private static readonly Expression<Func<RoomListing, HomeRoomDto>> BrowseProjection = l => new HomeRoomDto
    {
        Id = l.Id,
        UserId = l.UserId,
        PriceMonthly = l.PriceMonthly,
        RoomTypeName = l.RoomType.Name,
        ThumbnailUrl = l.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault(),
        CityName = l.City != null ? l.City.Name : null,
        DistrictName = l.District.Name,
        FurnishedStatus = l.FurnishedStatus,
        IsAc = l.IsAc,
        PriorityRank = l.PriorityRank,
        IsFeatured = l.IsFeatured,
        CreatedAt = l.CreatedAt,
    };

    // Home "for you": paid rank first, then newest live rows of the district (ix_roomlistings_district_active_recent).
    public async Task<List<HomeRoomDto>> GetHomeForYouProjectedAsync(Guid districtId, int limit)
        => await _dbSet.AsNoTracking()
            .Where(l => l.IsActive && !l.IsDeleted && l.DistrictId == districtId)
            .OrderByDescending(l => l.PriorityRank)
            .ThenByDescending(l => l.CreatedAt)
            .Take(limit)
            .Select(BrowseProjection)
            .ToListAsync();

    // District-free by design — the Home "Recently added" feed is identical for every user (ix_roomlistings_recent_active).
    public async Task<List<HomeRoomDto>> GetRecentProjectedAsync(int limit)
        => await _dbSet.AsNoTracking()
            .Where(l => l.IsActive && !l.IsDeleted)
            .OrderByDescending(l => l.PriorityRank)
            .ThenByDescending(l => l.CreatedAt)
            .Take(limit)
            .Select(BrowseProjection)
            .ToListAsync();

    public async Task<BrowsePage<HomeRoomDto>> SearchPagedAsync(
        Guid districtId, Guid? cityId, Guid? roomTypeId, string sortBy, int page, int pageSize, string? cursor)
    {
        page = Math.Clamp(page, 1, BrowseCursor.MaxPage);

        var query = _dbSet
            .AsNoTracking()
            .Where(l => l.IsActive && !l.IsDeleted && l.DistrictId == districtId);
        if (roomTypeId.HasValue)
            query = query.Where(l => l.RoomTypeId == roomTypeId.Value);

        if (sortBy is "price_asc" or "price_desc")
        {
            var cityFirst = query
                .OrderByDescending(l => l.PriorityRank)
                .ThenBy(l => cityId != null && l.CityId == cityId ? 0 : 1);
            var byPrice = sortBy == "price_asc"
                ? cityFirst.ThenBy(l => l.PriceMonthly).ThenBy(l => l.Id)
                : cityFirst.ThenByDescending(l => l.PriceMonthly).ThenByDescending(l => l.Id);
            var priced = await byPrice.Skip((page - 1) * pageSize).Take(pageSize + 1).Select(BrowseProjection).ToListAsync();
            return BrowseCursor.OffsetPage(priced, pageSize);
        }

        if (string.IsNullOrEmpty(cursor) && page > 1)
        {
            var legacy = await query
                .OrderByDescending(l => l.PriorityRank)
                .ThenBy(l => cityId != null && l.CityId == cityId ? 0 : 1)
                .ThenByDescending(l => l.CreatedAt)
                .ThenByDescending(l => l.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize + 1)
                .Select(BrowseProjection)
                .ToListAsync();
            return BrowseCursor.OffsetPage(legacy, pageSize);
        }

        // Keyset: one (rank, city|rest) phase at a time, each an index range scan in CreatedAt DESC order.
        var ranks = await PlanCatalogCache.RanksForAsync(context, ListingKinds.Room);
        var outsideRanks = BrowseCursor.OutsideRanks<RoomListing>(l => l.PriorityRank, ranks);
        var phases = BrowseCursor.Phases(ranks, cityId.HasValue);
        var (start, afterAt, afterId) = BrowseCursor.Resume(cursor, phases);

        var items = new List<HomeRoomDto>(pageSize + 1);
        var itemPhases = new List<BrowsePhase>(pageSize + 1);
        for (var i = start; i < phases.Count; i++)
        {
            var phase = phases[i];
            var need = pageSize + 1 - items.Count;
            var rank = phase.Rank;
            var phaseQuery = rank == BrowseCursor.OtherRanks
                ? query.Where(outsideRanks)
                : query.Where(l => l.PriorityRank == rank);
            if (cityId.HasValue)
            {
                var cid = cityId.Value;
                phaseQuery = phase.CityOnly
                    ? phaseQuery.Where(l => l.CityId == cid)
                    : phaseQuery.Where(l => l.CityId == null || l.CityId != cid);
            }
            if (afterAt.HasValue)
            {
                var at = afterAt.Value;
                var aid = afterId;
                phaseQuery = phaseQuery.Where(l => l.CreatedAt <= at && (l.CreatedAt < at || l.Id.CompareTo(aid) < 0));
            }

            var rows = await phaseQuery
                .OrderByDescending(l => l.CreatedAt)
                .ThenByDescending(l => l.Id)
                .Take(need)
                .Select(BrowseProjection)
                .ToListAsync();
            items.AddRange(rows);
            itemPhases.AddRange(Enumerable.Repeat(phase, rows.Count));
            afterAt = null;
            if (rows.Count == need) break;
        }

        return BrowseCursor.KeysetPage(items, itemPhases, pageSize, x => x.CreatedAt, x => x.Id);
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

    // Any IsDeleted on purpose: a soft-deleted approved room still locks the role.
    public async Task<bool> HasEverApprovedAsync(Guid userId)
        => await _dbSet
            .AsNoTracking()
            .AnyAsync(l => l.UserId == userId && l.LiveRequestStatus == GoLiveRequestStatuses.Approved);

    public async Task<IEnumerable<RoomListing>> GetActiveByUserIdAsync(Guid userId)
        => await _dbSet
            .AsNoTracking()
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
            .Include(l => l.User)
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

    // Public share-link/QR resolver — only a currently-live listing should stay reachable by slug.
    // IsActive (not just !IsDeleted) is required so a Pending or admin-Rejected listing's full public
    // page (owner name, exact GPS, address, photos) doesn't stay indefinitely reachable/indexable via
    // a previously-issued slug; matches the Share entry point itself only being offered while Live.
    public async Task<RoomListing?> GetBySlugWithPhotosAsync(string slug)
        => await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder))
            .FirstOrDefaultAsync(l => l.Slug == slug && !l.IsDeleted && l.IsActive);

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
