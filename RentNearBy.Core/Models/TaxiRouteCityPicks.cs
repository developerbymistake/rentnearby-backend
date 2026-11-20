using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Core.Models;

// Both ends of a route as the driver chose them: a key is a city from the live list, null is an other place.
public sealed record RouteCityPick(long? FromKey, long? ToKey);

public sealed record TaxiRouteCityError(int Status, string Type, string Message);

// Rules of the city list picker on PUT /taxis/me/routes. A route either names both ends (a city key or an other place)
// or neither (an old build: the server finds the city from the pin).
public static class TaxiRouteCityPicks
{
    // A city picked from the list carries its centre; a pin this far away is not that city.
    public const double PinMaxKm = 50;

    public const string BothMessage = "Choose a city or another place";
    public const string KeyMessage = "Choose a city from the list";
    public const string OneEndMessage = "Choose the From and To city again";
    public const string NotLiveMessage = "This city is not available. Choose a city from the list.";
    public const string FarPinMessage = "Choose the city again";
    public const string UnavailableMessage = "The city list is not available right now. Please try again.";

    public static string? ShapeError(TaxiRouteInput r)
    {
        if ((r.FromCityKey != null && r.FromOtherPlace) || (r.ToCityKey != null && r.ToOtherPlace)) return BothMessage;
        if (r.FromCityKey is <= 0 || r.ToCityKey is <= 0) return KeyMessage;
        return Named(r.FromCityKey, r.FromOtherPlace) != Named(r.ToCityKey, r.ToOtherPlace) ? OneEndMessage : null;
    }

    // null = an old build (no end named). Call after ShapeError passed.
    public static RouteCityPick? Of(TaxiRouteInput r)
        => Named(r.FromCityKey, r.FromOtherPlace) ? new RouteCityPick(r.FromCityKey, r.ToCityKey) : null;

    // A key the slot already holds is kept without a catalog check, so a city that left the list never blocks
    // saving the driver's other routes.
    public static bool NeedsCatalogCheck(long key, long? keyBefore) => key != keyBefore;

    // One end against the catalog answer for its key: Available = the catalog could be read; City = null when not live.
    public static TaxiRouteCityError? CheckEnd(decimal lat, decimal lng, bool available, TripCityInfo? city)
    {
        if (!available) return new TaxiRouteCityError(503, TripErrorTypes.CitiesUnavailable, UnavailableMessage);
        if (city is null) return Invalid(NotLiveMessage);
        var km = TaxiGeo.DistanceMeters((double)lat, (double)lng, city.Latitude, city.Longitude) / 1000;
        return km > PinMaxKm ? Invalid(FarPinMessage) : null;
    }

    public static TaxiRouteCityError Invalid(string message) => new(400, TaxiErrorTypes.TaxiRouteCityInvalid, message);

    private static bool Named(long? key, bool otherPlace) => key != null || otherPlace;
}
