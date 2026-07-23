using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class HomeHandlers
{
    private static readonly TimeSpan SummaryCacheTtl = TimeSpan.FromMinutes(3);
    // Same TTL as summary — both are cheap-to-recompute, best-effort caches, not a
    // correctness-sensitive value, so there's no reason for it to differ.
    private static readonly TimeSpan RecentCacheTtl = TimeSpan.FromMinutes(3);
    private const int DefaultLimit = 5;
    private const int MaxLimit = 20;
    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 30;

    private static readonly HashSet<string> RoomSortValues = new() { "newest", "price_asc", "price_desc" };
    private static readonly HashSet<string> PlotSortValues = new() { "newest", "area_asc", "area_desc" };

    private static string SummaryCacheKey(Guid districtId) => $"home:summary:{districtId}";
    // No districtId in the key — this feed is intentionally district-free, identical for
    // every caller, which is exactly what makes it a good caching candidate in the first place.
    private const string RecentRoomsCacheKey = "home:recentRooms";
    private const string RecentPlotsCacheKey = "home:recentPlots";

    private static int ClampLimit(int limit) => Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
    private static int ClampPage(int page) => Math.Max(page, 1);
    private static int ClampPageSize(int pageSize) => Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
    private static string ValidateRoomSort(string? sortBy) => RoomSortValues.Contains(sortBy ?? "") ? sortBy! : "newest";
    private static string ValidatePlotSort(string? sortBy) => PlotSortValues.Contains(sortBy ?? "") ? sortBy! : "newest";

    public static async Task<IResult> GetSummary(Guid districtId, ApplicationDbContext db, IServiceProvider sp)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        var cacheKey = SummaryCacheKey(districtId);

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(cacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<HomeSummaryDto>(cached!);
                    if (dto != null) return OkResponse(dto);
                }
                catch (JsonException) { /* corrupted cache entry — fall through to DB */ }
            }
        }

        var roomsCount = await db.RoomListings.CountAsync(l => l.DistrictId == districtId && l.IsActive && !l.IsDeleted);
        var plotsCount = await db.PlotListings.CountAsync(l => l.DistrictId == districtId && l.IsActive && !l.IsDeleted);

        var result = new HomeSummaryDto { RoomsCount = roomsCount, PlotsCount = plotsCount };

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(result);
            try { await redis.GetDatabase().StringSetAsync(cacheKey, json, SummaryCacheTtl); } catch { }
        }

        return OkResponse(result);
    }

    public static async Task<IResult> GetRooms(Guid districtId, int limit, IUnitOfWork unitOfWork)
    {
        var take = ClampLimit(limit);
        var items = await unitOfWork.RoomListings.SearchAsync(districtId, null, null, null, take);

        var result = items.Select(l => new HomeRoomDto
        {
            Id = l.Id,
            UserId = l.UserId,
            PriceMonthly = l.PriceMonthly,
            RoomTypeName = l.RoomType?.Name,
            ThumbnailUrl = l.Photos.FirstOrDefault()?.PhotoUrl,
            CityName = l.City?.Name,
            DistrictName = l.District?.Name ?? string.Empty,
            FurnishedStatus = l.FurnishedStatus,
            CreatedAt = l.CreatedAt,
        }).ToList();

        return OkResponse(new { items = result });
    }

    public static async Task<IResult> GetPlots(Guid districtId, int limit, IUnitOfWork unitOfWork)
    {
        var take = ClampLimit(limit);
        var (items, _) = await unitOfWork.PlotListings.GetAllAsync(page: 1, pageSize: take, isActive: true, districtId: districtId);

        var result = items.Select(p => new HomePlotDto
        {
            Id = p.Id,
            UserId = p.UserId,
            AreaValue = p.AreaValue,
            AreaUnit = p.AreaUnit,
            PlotTypeName = p.PlotType?.Name,
            ThumbnailUrl = p.Photos.FirstOrDefault()?.PhotoUrl,
            CityName = p.City?.Name,
            DistrictName = p.District?.Name ?? string.Empty,
            CreatedAt = p.CreatedAt,
        }).ToList();

        return OkResponse(new { items = result });
    }

    public static async Task<IResult> GetRoomsBrowse(
        Guid districtId, Guid? cityId, Guid? roomTypeId, string? sortBy, int page, int pageSize, IUnitOfWork unitOfWork)
    {
        var (items, hasMore) = await unitOfWork.RoomListings.SearchPagedAsync(
            districtId, cityId, roomTypeId, ValidateRoomSort(sortBy), ClampPage(page), ClampPageSize(pageSize));

        var result = items.Select(l => new HomeRoomDto
        {
            Id = l.Id,
            UserId = l.UserId,
            PriceMonthly = l.PriceMonthly,
            RoomTypeName = l.RoomType?.Name,
            ThumbnailUrl = l.Photos.FirstOrDefault()?.PhotoUrl,
            CityName = l.City?.Name,
            DistrictName = l.District?.Name ?? string.Empty,
            FurnishedStatus = l.FurnishedStatus,
            CreatedAt = l.CreatedAt,
        }).ToList();

        return OkResponse(new { items = result, hasMore });
    }

    public static async Task<IResult> GetPlotsBrowse(
        Guid districtId, Guid? cityId, Guid? plotTypeId, string? sortBy, int page, int pageSize, IUnitOfWork unitOfWork)
    {
        var (items, hasMore) = await unitOfWork.PlotListings.GetAllPagedByTypeIdAsync(
            districtId, cityId, plotTypeId, ValidatePlotSort(sortBy), ClampPage(page), ClampPageSize(pageSize));

        var result = items.Select(p => new HomePlotDto
        {
            Id = p.Id,
            UserId = p.UserId,
            AreaValue = p.AreaValue,
            AreaUnit = p.AreaUnit,
            PlotTypeName = p.PlotType?.Name,
            ThumbnailUrl = p.Photos.FirstOrDefault()?.PhotoUrl,
            CityName = p.City?.Name,
            DistrictName = p.District?.Name ?? string.Empty,
            CreatedAt = p.CreatedAt,
        }).ToList();

        return OkResponse(new { items = result, hasMore });
    }

    // Deliberately separate from GetRooms/GetRoomsBrowse rather than an optional-districtId
    // overload of either: this is a structurally different query (no district/city locality
    // to filter or rank by) and, being identical for every caller, is cached — GetRooms/
    // GetRoomsBrowse are per-district and were never worth caching the same way.
    public static async Task<IResult> GetRecentRooms(int limit, IUnitOfWork unitOfWork, IServiceProvider sp)
    {
        var take = ClampLimit(limit);
        var redis = sp.GetService<IConnectionMultiplexer>();

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(RecentRoomsCacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<List<HomeRoomDto>>(cached!);
                    if (dto != null) return OkResponse(new { items = dto.Take(take) });
                }
                catch (JsonException) { /* corrupted cache entry — fall through to DB */ }
            }
        }

        var items = await unitOfWork.RoomListings.GetRecentAsync(MaxLimit);
        var result = items.Select(l => new HomeRoomDto
        {
            Id = l.Id,
            UserId = l.UserId,
            PriceMonthly = l.PriceMonthly,
            RoomTypeName = l.RoomType?.Name,
            ThumbnailUrl = l.Photos.FirstOrDefault()?.PhotoUrl,
            CityName = l.City?.Name,
            DistrictName = l.District?.Name ?? string.Empty,
            FurnishedStatus = l.FurnishedStatus,
            CreatedAt = l.CreatedAt,
        }).ToList();

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(result);
            try { await redis.GetDatabase().StringSetAsync(RecentRoomsCacheKey, json, RecentCacheTtl); } catch { }
        }

        return OkResponse(new { items = result.Take(take) });
    }

    public static async Task<IResult> GetRecentPlots(int limit, IUnitOfWork unitOfWork, IServiceProvider sp)
    {
        var take = ClampLimit(limit);
        var redis = sp.GetService<IConnectionMultiplexer>();

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(RecentPlotsCacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<List<HomePlotDto>>(cached!);
                    if (dto != null) return OkResponse(new { items = dto.Take(take) });
                }
                catch (JsonException) { /* corrupted cache entry — fall through to DB */ }
            }
        }

        var items = await unitOfWork.PlotListings.GetRecentAsync(MaxLimit);
        var result = items.Select(p => new HomePlotDto
        {
            Id = p.Id,
            UserId = p.UserId,
            AreaValue = p.AreaValue,
            AreaUnit = p.AreaUnit,
            PlotTypeName = p.PlotType?.Name,
            ThumbnailUrl = p.Photos.FirstOrDefault()?.PhotoUrl,
            CityName = p.City?.Name,
            DistrictName = p.District?.Name ?? string.Empty,
            CreatedAt = p.CreatedAt,
        }).ToList();

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(result);
            try { await redis.GetDatabase().StringSetAsync(RecentPlotsCacheKey, json, RecentCacheTtl); } catch { }
        }

        return OkResponse(new { items = result.Take(take) });
    }
}
