using System.Text.Json;
using RentNearBy.Core.DTOs.TaxiRequests;

namespace RentNearBy.Core.Models.TaxiRequests;

public sealed record TaxiSnapshotPickup(string Name, DateOnly Date);

public sealed record TaxiSnapshotDrop(string Name, bool SameAsPickup, DateOnly Date);

// From = the city's first day, To = the morning after its last day (every day has a night).
public sealed record TaxiSnapshotCity(long CityKey, string Name, int Nights, DateOnly From, DateOnly To);

public sealed record TaxiSnapshotDay(int Day, DateOnly Date, string City, bool Taxi, int Km, int Minutes, IReadOnlyList<string> Places);

// The trip as the driver sees it, frozen when the request is created (TaxiRequests.TripSnapshotJson). Names, dates, cities
// and places come from the stored plan and the server's pickup lists; the body adds only km and minutes per day (P20).
public sealed record TaxiTripSnapshot(
    string Name,
    TaxiSnapshotPickup? Pickup,
    TaxiSnapshotDrop? Drop,
    IReadOnlyList<TaxiSnapshotCity> Cities,
    IReadOnlyList<TaxiSnapshotDay> Days)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TaxiTripSnapshot Build(TripPlan plan, DateOnly tripStart, string tripName, CreateTaxiRequestDto body, string? pickupName, string? dropName)
    {
        var planDays = (plan.Days ?? []).Where(d => d != null).OrderBy(d => d.Day).ToList();
        var dayCount = planDays.Count;
        var pickupOn = body.Pickup?.On == true;
        var dropOn = body.Drop?.On == true;
        var sightseeing = body.Sightseeing?.On == true ? (body.Sightseeing.Days ?? []).ToHashSet() : [];
        var stats = (body.DayStats ?? []).GroupBy(s => s.Day).ToDictionary(g => g.Key, g => g.First());

        var days = planDays.Select(d =>
        {
            var stat = stats.GetValueOrDefault(d.Day);
            var taxi = sightseeing.Contains(d.Day) || (pickupOn && d.Day == 1) || (dropOn && d.Day == dayCount);
            return new TaxiSnapshotDay(
                d.Day,
                TaxiRequestCreateRules.DateOf(tripStart, d.Day),
                CityOf(plan, d.Day),
                taxi,
                Math.Clamp(stat?.Km ?? 0, 0, TaxiDispatchRules.MaxDayKm),
                Math.Clamp(stat?.Minutes ?? 0, 0, TaxiDispatchRules.MaxDayMinutes),
                (d.Spots ?? []).Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name)).Select(s => s.Name.Trim()).ToList());
        }).ToList();

        var cities = new List<TaxiSnapshotCity>();
        foreach (var leg in plan.Legs ?? [])
        {
            var first = plan.LegStart(leg.Index);
            var last = plan.LegEnd(leg.Index);
            if (first < 1) continue;
            cities.Add(new TaxiSnapshotCity(leg.CityKey, leg.CityName, last - first + 1,
                TaxiRequestCreateRules.DateOf(tripStart, first), TaxiRequestCreateRules.DateOf(tripStart, last).AddDays(1)));
        }

        var pickup = pickupOn && pickupName is not null ? new TaxiSnapshotPickup(pickupName, TaxiRequestCreateRules.DateOf(tripStart, 1)) : null;
        var sameAsPickup = dropOn && body.Drop!.SameAsPickup;
        var dropText = sameAsPickup ? pickupName : dropName;
        var drop = dropOn && dropText is not null
            ? new TaxiSnapshotDrop(dropText, sameAsPickup, TaxiRequestCreateRules.DateOf(tripStart, Math.Max(1, dayCount)))
            : null;

        return new TaxiTripSnapshot(tripName, pickup, drop, cities, days);
    }

    public IReadOnlyList<string> CityNames => Cities.Select(c => c.Name).ToList();

    public TaxiRingSnapshot ToRing(TaxiDateRange occupied, bool pickup, bool sightseeing, bool drop, int sightseeingDayCount, int seats, int travellers)
        => new(occupied.From, occupied.To, Pickup?.Name, CityNames, pickup, sightseeing, drop, sightseeingDayCount, seats, travellers);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static TaxiTripSnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TaxiTripSnapshot>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CityOf(TripPlan plan, int day)
        => plan.LegOf(day)?.CityName is { Length: > 0 } name ? name : plan.CityName;
}
