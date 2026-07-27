using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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

        if (existingNearestCity is not null) return (reverse.DisplayName, existingNearestCity);

        if (string.IsNullOrWhiteSpace(reverse.PlaceName)) return (reverse.DisplayName, null);

        var name = reverse.PlaceName.Length > 100 ? reverse.PlaceName[..100] : reverse.PlaceName;

        var match = existingCities.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return (reverse.DisplayName, match);

        var city = new City
        {
            Id = Guid.NewGuid(),
            DistrictId = district.Id,
            Name = name,
            Latitude = (decimal)lat,
            Longitude = (decimal)lng,
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            _db.Cities.Add(city);
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            var existing = await _db.Cities.AsNoTracking()
                .FirstOrDefaultAsync(c => c.DistrictId == district.Id && c.Name == name);
            if (existing is not null) return (reverse.DisplayName, existing);
            return (reverse.DisplayName, null);
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
}
