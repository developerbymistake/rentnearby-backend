using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ICityResolutionService
{
    Task<(string? Address, City? City)> ResolveAddressAndCityAsync(
        District district, List<City> existingCities, City? existingNearestCity, double lat, double lng);
}
