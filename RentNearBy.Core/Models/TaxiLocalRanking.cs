namespace RentNearBy.Core.Models;

// What a trip day asks of a local taxi: the group, the day's road km and where the day starts. 0 / null = not asked.
public sealed record TaxiLocalNeed(int People = 0, int DayKm = 0, double? Lat = null, double? Lng = null)
{
    public bool IsEmpty => People <= 0 && DayKm <= 0 && !HasPoint;
    public bool HasPoint => Lat is >= -90 and <= 90 && Lng is >= -180 and <= 180;
}

// One driver against a need. Cars: his own vehicle once per started group of its seats (unknown seats = one car).
// ExtraKm: km over his day limit (null when the limit or the day km is unknown); no per-km price is stored, so none is invented.
public readonly record struct TaxiLocalFit(bool SeatsFit, bool KmFit, int Cars, int? DayCost, int? ExtraKm, double? PickupKm);

public sealed record TaxiLocalCandidate(
    Guid Id, short? Seats, int? DayRate, int? DayKmLimit, short PriorityRank, bool InCity, double? BaseLat, double? BaseLng);

public static class TaxiLocalRanking
{
    public static TaxiLocalFit Evaluate(TaxiLocalCandidate c, TaxiLocalNeed need)
    {
        var seatsFit = need.People <= 0 || c.Seats is { } s && s >= need.People;
        var cars = TaxiWaysRanking.CarsNeeded(need.People, c.Seats);
        int? extraKm = need.DayKm > 0 && c.DayKmLimit is { } limit ? Math.Max(0, need.DayKm - limit) : null;
        var kmFit = need.DayKm <= 0 || extraKm == 0;
        int? dayCost = c.DayRate is { } rate ? rate * cars : null;
        double? pickupKm = need.HasPoint && c.BaseLat is { } lat && c.BaseLng is { } lng
            ? Math.Round(TaxiGeo.DistanceMeters(need.Lat!.Value, need.Lng!.Value, lat, lng) / 1000, 1)
            : null;
        return new TaxiLocalFit(seatsFit, kmFit, cars, dayCost, extraKm, pickupKm);
    }

    // Seats fit, km fit, plan rank, the trip's city, the day's cost for the group, nearest base, id.
    public static int Compare(TaxiLocalCandidate x, TaxiLocalFit fx, TaxiLocalCandidate y, TaxiLocalFit fy)
    {
        var c = fy.SeatsFit.CompareTo(fx.SeatsFit);
        if (c != 0) return c;
        c = fy.KmFit.CompareTo(fx.KmFit);
        if (c != 0) return c;
        c = y.PriorityRank.CompareTo(x.PriorityRank);
        if (c != 0) return c;
        c = y.InCity.CompareTo(x.InCity);
        if (c != 0) return c;
        c = NullLast(fx.DayCost, fy.DayCost);
        if (c != 0) return c;
        c = NullLast(fx.PickupKm, fy.PickupKm);
        return c != 0 ? c : x.Id.CompareTo(y.Id);
    }

    public static List<(TaxiLocalCandidate Taxi, TaxiLocalFit Fit)> Rank(IEnumerable<TaxiLocalCandidate> taxis, TaxiLocalNeed need)
    {
        var list = taxis.Select(t => (Taxi: t, Fit: Evaluate(t, need))).ToList();
        list.Sort((a, b) => Compare(a.Taxi, a.Fit, b.Taxi, b.Fit));
        return list;
    }

    private static int NullLast<T>(T? a, T? b) where T : struct, IComparable<T>
    {
        if (a is { } x && b is { } y) return x.CompareTo(y);
        return a.HasValue ? -1 : b.HasValue ? 1 : 0;
    }
}
