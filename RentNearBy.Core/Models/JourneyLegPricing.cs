namespace RentNearBy.Core.Models;

// One taxi on one saved stretch, priced on the server: PerCarFare = the road-length share of his route's quote (TaxiFare).
public sealed record LegTaxiFare(Guid TaxiId, Guid RouteId, short? Seats, int PerCarFare, bool Partial);

// TaxiIds: the cars that carry the group, the chosen driver first. Fare: the whole stretch for the group.
public sealed record JourneyLegQuote(int Fare, int Cars, IReadOnlyList<Guid> TaxiIds, bool Covered)
{
    public IReadOnlyList<Guid> CoverOthers => TaxiIds.Count > 1 ? TaxiIds.Skip(1).ToList() : [];
}

// The seat cover (TaxiSeatCover) is the only source of a saved stretch's price, cars and lead texts.
public static class JourneyLegPricing
{
    // The driver is always in the cover; the other cars come only from `cover` (what the traveller accepted), in that order.
    public static JourneyLegQuote Quote(LegTaxiFare driver, IReadOnlyList<LegTaxiFare> cover, int people)
    {
        var others = cover
            .Where(c => c.TaxiId != driver.TaxiId)
            .DistinctBy(c => c.TaxiId)
            .ToList();
        var options = others.Select((c, i) => new SeatOption(i + 1, c.Seats, c.PerCarFare)).ToList();
        var result = TaxiSeatCover.SolveWith(new SeatOption(0, driver.Seats, driver.PerCarFare), options, people);
        var ids = result.Chosen.Select(rank => rank == 0 ? driver.TaxiId : others[rank - 1].TaxiId).ToList();
        return new JourneyLegQuote(result.Fare, result.Cars, ids, result.Covered);
    }

    // Cars of a saved stretch: one per covered car, else the driver's own count for the whole group.
    public static int CarsOfLeg(int people, short? driverSeats, IReadOnlyCollection<Guid> coverOthers)
        => coverOthers.Count > 0 ? 1 + coverOthers.Count : TaxiWaysRanking.CarsNeeded(people, driverSeats);

    // Cars one asked driver brings: one when he is a car of the leg's cover, else his own count for the whole group.
    public static int CarsFor(Guid taxiId, short? seats, int people, Guid driverTaxiId, IReadOnlyCollection<Guid> coverOthers)
        => coverOthers.Count > 0 && (taxiId == driverTaxiId || coverOthers.Contains(taxiId))
            ? 1
            : TaxiWaysRanking.CarsNeeded(people, seats);

    public static int LeadFare(int perCarFare, int cars) => TripExecutionRules.Saturate((long)perCarFare * Math.Max(1, cars));
}
