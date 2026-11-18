using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

// Available = false: no Atlas city list right now (never guess). City = null: the point is outside every live city's guard.
public sealed record CityPin(bool Available, TripCityInfo? City);

// Trusted = false when the city is only a guess (the parent rule fell back to the nearest city because the geocoder failed).
public sealed record TrustedCityPin(bool Available, TripCityInfo? City, bool Trusted);

// The single nearest Atlas city rule for stays and taxis, over the cached catalog grid. No Atlas call per request.
public interface ICityResolver
{
    // Box half-size (km) around a city centre that holds every stay and taxi whose nearest city it is.
    double BoxKm { get; }
    Task<CityPin> ResolveAsync(double lat, double lng);
    // The live city for a key; City = null when the key is not a live city.
    Task<CityPin> FindAsync(long cityKey);
    // Items whose own nearest city is cityKey; empty when unavailable.
    Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey);
    // Fills the parent-city cache for a pin (call when a listing is saved); never throws.
    Task WarmAsync(double lat, double lng);

    // For values that are stored (taxi route city keys): only a trusted city may be kept.
    async Task<TrustedCityPin> ResolveTrustedAsync(double lat, double lng)
    {
        var pin = await ResolveAsync(lat, lng);
        return new TrustedCityPin(pin.Available, pin.City, pin.Available && pin.City != null);
    }
}
