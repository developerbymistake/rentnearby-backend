using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;

namespace RentNearBy.Api.Handlers;

// /context split by what each part depends on: the district is point-in-polygon on the exact point every call
// (one probe of the partial GiST index), the district payload (fields + cities) is cached per district id,
// and the nearest city is computed per call from that payload. Nothing point-dependent is ever cached.
internal static class LocationContext
{
    private static readonly TimeSpan PayloadTtl = TimeSpan.FromMinutes(10);

    internal sealed record DistrictRow(Guid Id, string Name, string StateName, bool BusinessOnboardingEnabled);
    internal sealed record CityRow(Guid Id, Guid DistrictId, string Name, decimal? Latitude, decimal? Longitude);
    internal sealed record Payload(DistrictRow District, List<CityRow> Cities);

    public static async Task<Guid?> FindDistrictIdAsync(ApplicationDbContext db, double lat, double lng)
    {
        var point = new NetTopologySuite.Geometries.Point(lng, lat) { SRID = 4326 };
        return await db.Districts
            .AsNoTracking()
            .Where(d => d.IsActive && d.Boundary != null && d.Boundary.Contains(point))
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync();
    }

    public static async Task<Payload?> GetPayloadAsync(ApplicationDbContext db, IConnectionMultiplexer? redis, Guid districtId)
    {
        var key = ListingCacheKeys.ContextDistrict(districtId);
        if (redis != null)
        {
            try
            {
                var cached = await redis.GetDatabase().StringGetAsync(key);
                if (cached.HasValue && JsonSerializer.Deserialize<Payload>(cached!) is { } hit) return hit;
            }
            catch { }
        }

        var district = await db.Districts
            .AsNoTracking()
            .Where(d => d.Id == districtId)
            .Select(d => new DistrictRow(d.Id, d.Name, d.StateName, d.BusinessOnboardingEnabled))
            .FirstOrDefaultAsync();
        if (district == null) return null;

        var cities = await db.Cities
            .AsNoTracking()
            .Where(c => c.DistrictId == districtId)
            .OrderBy(c => c.Name)
            .Select(c => new CityRow(c.Id, c.DistrictId, c.Name, c.Latitude, c.Longitude))
            .ToListAsync();

        var payload = new Payload(district, cities);
        if (redis != null)
        {
            try { await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(payload), PayloadTtl); } catch { }
        }
        return payload;
    }

    // Nearest city with coordinates; none has coordinates = the first city (alphabetical), as before.
    public static Guid? NearestCityId(Payload payload, double lat, double lng)
    {
        var points = payload.Cities
            .Select(c => ((double?)c.Latitude, (double?)c.Longitude))
            .ToList();
        var index = GeoCacheKey.NearestIndex(points, lat, lng);
        return index is int i ? payload.Cities[i].Id : payload.Cities.FirstOrDefault()?.Id;
    }

    public static object Response(Payload payload, Guid? nearestCityId) => new
    {
        district = new
        {
            id = payload.District.Id,
            name = payload.District.Name,
            stateName = payload.District.StateName,
            businessOnboardingEnabled = payload.District.BusinessOnboardingEnabled,
        },
        nearestCityId,
        cities = payload.Cities.Select(c => new { id = c.Id, districtId = c.DistrictId, name = c.Name, latitude = c.Latitude, longitude = c.Longitude }).ToList(),
    };
}
