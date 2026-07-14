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
    private const int DefaultLimit = 5;
    private const int MaxLimit = 20;

    private static string SummaryCacheKey(Guid districtId) => $"home:summary:{districtId}";

    private static int ClampLimit(int limit) => Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);

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
}
