using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Handlers;

// Server fares of taxis on one saved stretch; a client fare is never used. The ways network first (the very links the
// traveller was shown), else the stored route match (FindOnLegAsync). Seats are the live ones when the taxi row is loaded.
internal static class JourneyLegFares
{
    public static async Task<Dictionary<Guid, LegTaxiFare>> LoadAsync(
        ITaxiWaysSearch ways, ISavedJourneyRepository journeys, string legKey,
        double fromLat, double fromLng, double toLat, double toLng,
        IEnumerable<Guid> taxiIds, IReadOnlyDictionary<Guid, JourneyTaxiInfo> taxis, Guid? excludeRoute = null)
    {
        var ids = taxiIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var fares = await ways.LegFaresAsync(legKey, ids, excludeRoute, default);
        foreach (var id in ids.Where(i => !fares.ContainsKey(i)))
        {
            var match = await journeys.FindOnLegAsync(id, fromLat, fromLng, toLat, toLng);
            if (match != null) fares[id] = new LegTaxiFare(id, match.RouteId, null, match.Fare, match.Partial);
        }
        foreach (var id in fares.Keys.ToList())
            if (taxis.TryGetValue(id, out var taxi))
                fares[id] = fares[id] with { Seats = taxi.Seats };
        return fares;
    }

    public static Task<Dictionary<Guid, LegTaxiFare>> LoadAsync(
        ITaxiWaysSearch ways, ISavedJourneyRepository journeys, SavedJourneyLeg leg,
        IEnumerable<Guid> taxiIds, IReadOnlyDictionary<Guid, JourneyTaxiInfo> taxis, Guid? excludeRoute = null)
        => LoadAsync(ways, journeys, leg.LegKey, (double)leg.FromLatitude, (double)leg.FromLongitude,
            (double)leg.ToLatitude, (double)leg.ToLongitude, taxiIds, taxis, excludeRoute);

    // The leg's cover over its driver and the cover cars still on the stretch; null when the driver has no server fare.
    public static JourneyLegQuote? Quote(Guid driverId, IEnumerable<Guid> coverOthers, Dictionary<Guid, LegTaxiFare> fares, int people)
    {
        if (!fares.TryGetValue(driverId, out var driver)) return null;
        var cover = coverOthers.Where(fares.ContainsKey).Select(id => fares[id]).ToList();
        return JourneyLegPricing.Quote(driver, cover, people);
    }
}
