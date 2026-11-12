using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Core.Models;

// What a trip day asks of a local taxi: the group, the day's road km and where the day starts. 0 / null = not asked.
public sealed record TaxiLocalNeed(int People = 0, int DayKm = 0, double? Lat = null, double? Lng = null, double? RadiusKm = null)
{
    public bool IsEmpty => People <= 0 && DayKm <= 0 && !HasPoint;
    public double? PickupLimitKm => HasPoint && RadiusKm is > 0 ? RadiusKm : null;
    public bool HasPoint => Lat is >= -90 and <= 90 && Lng is >= -180 and <= 180;
}

// GET /taxis/browse need params: people 0..JourneyRules.MaxPeople, dayKm 0..MaxDayKm, lat and lng both or neither.
public static class TaxiLocalNeedRules
{
    public const int MaxDayKm = 1000;
    public const double MaxRadiusKm = 200;

    public static string? Error(int? people, int? dayKm, double? lat, double? lng, double? radiusKm = null)
    {
        if (radiusKm is { } r0 && (double.IsNaN(r0) || r0 < 0 || r0 > MaxRadiusKm)) return $"Radius must be 0 to {MaxRadiusKm} km";
        if (people is < 0 or > JourneyRules.MaxPeople) return $"People must be 0 to {JourneyRules.MaxPeople}";
        if (dayKm is < 0 or > MaxDayKm) return $"Day km must be 0 to {MaxDayKm}";
        if (lat.HasValue != lng.HasValue) return "Send both lat and lng";
        if (lat is { } a && (double.IsNaN(a) || a < -90 || a > 90)) return "Invalid coordinates";
        if (lng is { } b && (double.IsNaN(b) || b < -180 || b > 180)) return "Invalid coordinates";
        return null;
    }

    // Null when nothing is asked, so the plain order and the plain cache key stay as before.
    public static TaxiLocalNeed? Build(int? people, int? dayKm, double? lat, double? lng, double? radiusKm = null)
    {
        var need = new TaxiLocalNeed(people ?? 0, dayKm ?? 0, lat, lng, radiusKm);
        return need.IsEmpty ? null : need;
    }
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

// A taxi card with its own base pin, for the city rule.
public sealed record TaxiAt(TaxiBrowseDto Item, double Lat, double Lng);

// One city's taxis (already cut by the single-city rule): category filter, then the plain order or the need ranking, then the page.
// The pin only ranks (pickup km), it never cuts: a need without a radius has no pickup limit.
public static class TaxiCityList
{
    public static (List<TaxiBrowseDto> Items, bool HasMore) Page(
        IReadOnlyList<TaxiAt> all, string? category, TaxiLocalNeed? need, int page, int pageSize)
    {
        var rows = category == null ? all.ToList() : all.Where(t => t.Item.VehicleCategory == category).ToList();
        List<TaxiAt> ordered;
        Dictionary<Guid, TaxiLocalFit>? fits = null;
        if (need is { IsEmpty: false })
        {
            var ranked = TaxiLocalRanking.Rank(
                rows.Select(t => new TaxiLocalCandidate(t.Item.Id, t.Item.Seats, t.Item.LocalDayRate, t.Item.LocalDayKm, t.Item.PriorityRank, true, t.Lat, t.Lng)),
                need);
            var byId = rows.ToDictionary(t => t.Item.Id);
            ordered = ranked.Select(r => byId[r.Taxi.Id]).ToList();
            fits = ranked.ToDictionary(r => r.Taxi.Id, r => r.Fit);
        }
        else ordered = rows;

        var skip = (Math.Max(page, 1) - 1) * pageSize;
        var items = ordered.Skip(skip).Take(pageSize).Select(t =>
        {
            if (fits != null && fits.TryGetValue(t.Item.Id, out var f))
            {
                t.Item.TripSeatsFit = f.SeatsFit; t.Item.TripCars = f.Cars; t.Item.TripDayCost = f.DayCost;
                t.Item.TripExtraKm = f.ExtraKm; t.Item.TripPickupKm = f.PickupKm;
            }
            return t.Item;
        }).ToList();
        return (items, ordered.Count > skip + items.Count);
    }
}
