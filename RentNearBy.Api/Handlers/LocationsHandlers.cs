using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class LocationsHandlers
{
    public static async Task<IResult> GetContext(double lat, double lng, IServiceProvider sp, bool includeAddress = false)
    {
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
            return BadRequestResponse("Invalid coordinates");

        var db = sp.GetRequiredService<ApplicationDbContext>();
        var districtId = await LocationContext.FindDistrictIdAsync(db, lat, lng);
        if (districtId is not Guid id)
            return NotFoundResponse("No district found for given coordinates");

        if (!includeAddress)
        {
            var payload = await LocationContext.GetPayloadAsync(db, sp.GetService<IConnectionMultiplexer>(), id);
            if (payload == null) return NotFoundResponse("No district found for given coordinates");
            return OkResponse(LocationContext.Response(payload, LocationContext.NearestCityId(payload, lat, lng)));
        }

        var match = await db.Districts
            .AsNoTracking()
            .Include(d => d.Cities.OrderBy(c => c.Name))
            .FirstOrDefaultAsync(d => d.Id == id);
        if (match == null)
            return NotFoundResponse("No district found for given coordinates");

        var cities = match.Cities.ToList();
        var nearestIndex = GeoCacheKey.NearestIndex(
            cities.Select(c => ((double?)c.Latitude, (double?)c.Longitude)).ToList(), lat, lng);
        var nearestCity = nearestIndex is int n ? cities[n] : cities.FirstOrDefault();

        var cityResolver = sp.GetRequiredService<ICityResolutionService>();
        var (address, resolvedCity) = await cityResolver.ResolveAddressAndCityAsync(match, cities, nearestCity, lat, lng);

        var responseCities = cities;
        if (resolvedCity is not null && cities.All(c => c.Id != resolvedCity.Id))
        {
            responseCities = new List<City>(cities) { resolvedCity };
        }

        return OkResponse(new
        {
            district = new { id = match.Id, name = match.Name, stateName = match.StateName, businessOnboardingEnabled = match.BusinessOnboardingEnabled },
            nearestCityId = resolvedCity?.Id ?? nearestCity?.Id,
            cities = responseCities.Select(c => new { id = c.Id, districtId = c.DistrictId, name = c.Name, latitude = c.Latitude, longitude = c.Longitude }).ToList(),
            address,
        });
    }
}
