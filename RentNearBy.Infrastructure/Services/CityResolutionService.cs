using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.Geometries;
using StackExchange.Redis;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

public class CityResolutionService : ICityResolutionService
{
    private readonly ApplicationDbContext _db;
    private readonly IGeocodingService _geocoding;
    private readonly IMemoryCache _cache;
    private readonly IConnectionMultiplexer? _redis;

    public CityResolutionService(ApplicationDbContext db, IGeocodingService geocoding, IMemoryCache cache, IConnectionMultiplexer? redis = null)
    {
        _db = db;
        _geocoding = geocoding;
        _cache = cache;
        _redis = redis;
    }

    public async Task<(string? Address, City? City)> ResolveAddressAndCityAsync(
        District district, List<City> existingCities, City? existingNearestCity, double lat, double lng)
    {
        var reverse = await _geocoding.ReverseGeocodeAsync(lat, lng);
        if (reverse is null) return (null, existingNearestCity);

        if (string.IsNullOrWhiteSpace(reverse.PlaceName)) return (reverse.DisplayName, existingNearestCity);

        var name = reverse.PlaceName.Length > 100 ? reverse.PlaceName[..100] : reverse.PlaceName;

        var match = existingCities.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return (reverse.DisplayName, match);

        var (verifiedLat, verifiedLng) = await ResolveVerifiedCoordinatesAsync(district, name, lat, lng);

        var city = new City
        {
            Id = Guid.NewGuid(),
            DistrictId = district.Id,
            Name = name,
            Latitude = verifiedLat,
            Longitude = verifiedLng,
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            _db.Cities.Add(city);
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The unique index is on (DistrictId, lower(Name)) — see the City entity config in
            // ApplicationDbContext — so this fires for ANY case-variant collision, not just an
            // exact-name race. Query the same indexed NameLower shadow column rather than
            // EF.Functions.Lower()/.ToLower() on Name, so this is an index seek, not a scan.
            var lowerName = name.ToLowerInvariant();
            var existing = await _db.Cities.AsNoTracking()
                .FirstOrDefaultAsync(c => c.DistrictId == district.Id && EF.Property<string>(c, "NameLower") == lowerName);
            if (existing is not null) return (reverse.DisplayName, existing);
            return (reverse.DisplayName, existingNearestCity);
        }

        await BustCachesAsync(district.Id);
        return (reverse.DisplayName, city);
    }

    private async Task BustCachesAsync(Guid districtId)
    {
        _cache.Remove($"cities_{districtId}");
        _cache.Remove("cities_all");

        if (_redis is null) return;
        try
        {
            var server = _redis.GetServers().FirstOrDefault(s => s.IsConnected);
            if (server is null) return;
            var redisDb = _redis.GetDatabase();
            await foreach (var key in server.KeysAsync(pattern: "context:*"))
                await redisDb.KeyDeleteAsync(key);
            await foreach (var key in server.KeysAsync(pattern: "plot_context:*"))
                await redisDb.KeyDeleteAsync(key);
        }
        catch { }
    }

    private static readonly string[] TypePriority = ["city", "suburb", "town", "village", "administrative"];

    private static int PriorityRank(string? type)
    {
        if (type is null) return TypePriority.Length;
        var idx = Array.IndexOf(TypePriority, type.ToLowerInvariant());
        return idx >= 0 ? idx : TypePriority.Length;
    }

    private async Task<(decimal Latitude, decimal Longitude)> ResolveVerifiedCoordinatesAsync(
        District district, string name, double fallbackLat, double fallbackLng)
    {
        if (district.Boundary is null) return ((decimal)fallbackLat, (decimal)fallbackLng);

        var candidates = await _geocoding.SearchPlacesAsync($"{name}, {district.Name}, India", limit: 10);
        var best = candidates
            .Where(c => district.Boundary.Contains(new Point((double)c.Longitude, (double)c.Latitude) { SRID = 4326 }))
            .OrderBy(c => PriorityRank(c.Type))
            .FirstOrDefault();

        return best is not null
            ? (best.Latitude, best.Longitude)
            : ((decimal)fallbackLat, (decimal)fallbackLng);
    }
}
