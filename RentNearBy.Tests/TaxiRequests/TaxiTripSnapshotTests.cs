using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiTripSnapshotTests
{
    private static readonly DateOnly Start = new(2026, 12, 18);

    // The mock trip: Nainital days 1 to 3, Ranikhet day 4 (contract driver detail sample).
    private static TripPlan Plan()
    {
        var plan = new TripPlan
        {
            Legs =
            [
                new TripCityLeg { Index = 1, CityKey = 632970, CityName = "Nainital" },
                new TripCityLeg { Index = 2, CityKey = 1418230, CityName = "Ranikhet" },
            ],
            Settings = new TripSettings { StartDate = Start, Adults = 2 },
        };
        string[][] places =
        [
            ["Naini Lake", "Naina Devi Temple", "Snow View Point", "Mall Road"],
            ["Bhimtal Lake", "Sattal", "Kainchi Dham", "Nainital Zoo"],
            ["Tiffin Top", "Hanuman Garhi", "Himalaya Darshan", "Raj Bhavan"],
            ["Chaubatia Gardens", "Jhula Devi Temple", "Ranikhet Golf Course"],
        ];
        for (var d = 1; d <= 4; d++)
            plan.Days.Add(new TripDay { Day = d, Leg = d == 4 ? 2 : 1, Spots = places[d - 1].Select((n, i) => new TripSpot { Id = $"s{d}{i}", Name = n }).ToList() });
        plan.Normalize();
        return plan;
    }

    private static CreateTaxiRequestDto Body() => new()
    {
        Pickup = new CreateTaxiPickupDto { On = true, CityKey = 6377270 },
        Sightseeing = new CreateTaxiSightseeingDto { On = true, Days = [1, 2, 4] },
        Drop = new CreateTaxiDropDto { On = true, SameAsPickup = true },
        Seats = 4,
        DayStats =
        [
            new TaxiDayStatDto { Day = 1, Km = 25, Minutes = 370 },
            new TaxiDayStatDto { Day = 2, Km = 30, Minutes = 560 },
            new TaxiDayStatDto { Day = 3, Km = 40, Minutes = 300 },
            new TaxiDayStatDto { Day = 4, Km = 18, Minutes = 230 },
        ],
    };

    [Fact]
    public void Snapshot_matches_the_contract_driver_detail_trip()
    {
        var s = TaxiTripSnapshot.Build(Plan(), Start, "Uttarakhand Hills Trip", Body(), "Kathgodam", null);

        Assert.Equal("Uttarakhand Hills Trip", s.Name);
        Assert.Equal(new TaxiSnapshotPickup("Kathgodam", Start), s.Pickup);
        Assert.Equal(new TaxiSnapshotDrop("Kathgodam", true, Start.AddDays(3)), s.Drop);
        Assert.Equal(
            [new TaxiSnapshotCity(632970, "Nainital", 3, Start, Start.AddDays(3)), new TaxiSnapshotCity(1418230, "Ranikhet", 1, Start.AddDays(3), Start.AddDays(4))],
            s.Cities);
        Assert.Equal([true, true, false, true], s.Days.Select(d => d.Taxi));
        Assert.Equal(["Nainital", "Nainital", "Nainital", "Ranikhet"], s.Days.Select(d => d.City));
        Assert.Equal([25, 30, 40, 18], s.Days.Select(d => d.Km));
        Assert.Equal([370, 560, 300, 230], s.Days.Select(d => d.Minutes));
        Assert.Equal(["Chaubatia Gardens", "Jhula Devi Temple", "Ranikhet Golf Course"], s.Days[3].Places);
        Assert.Equal(Start.AddDays(1), s.Days[1].Date);
    }

    [Fact]
    public void Snapshot_reads_names_from_the_plan_not_the_body()
    {
        var body = Body();
        body.DayStats.Add(new TaxiDayStatDto { Day = 9, Km = 99, Minutes = 99 });
        body.Sightseeing.Days = [1, 2, 4, 9];
        var plan = Plan();

        var s = TaxiTripSnapshot.Build(plan, Start, "Stored title", body, "Kathgodam", null);

        Assert.Equal("Stored title", s.Name);
        Assert.Equal([1, 2, 3, 4], s.Days.Select(d => d.Day));
        Assert.Equal(plan.Days.SelectMany(d => d.Spots).Select(p => p.Name), s.Days.SelectMany(d => d.Places));
        Assert.Equal(["Nainital", "Ranikhet"], s.CityNames);
        Assert.Equal(Start.AddDays(3), s.Days[^1].Date);
    }

    [Fact]
    public void Day_stats_are_clamped_and_a_missing_day_reads_zero()
    {
        var body = Body();
        body.DayStats = [new TaxiDayStatDto { Day = 1, Km = 5000, Minutes = -3 }];
        var s = TaxiTripSnapshot.Build(Plan(), Start, "Trip", body, "Kathgodam", null);
        Assert.Equal((TaxiDispatchRules.MaxDayKm, 0), (s.Days[0].Km, s.Days[0].Minutes));
        Assert.Equal((0, 0), (s.Days[1].Km, s.Days[1].Minutes));
    }

    [Fact]
    public void Sections_off_leave_pickup_and_drop_empty()
    {
        var body = Body();
        body.Pickup.On = false;
        body.Drop.On = false;
        var s = TaxiTripSnapshot.Build(Plan(), Start, "Trip", body, null, null);
        Assert.Null(s.Pickup);
        Assert.Null(s.Drop);
        Assert.Equal([true, true, false, true], s.Days.Select(d => d.Taxi));
    }

    [Fact]
    public void Drop_to_another_city_uses_the_drop_name()
    {
        var body = Body();
        body.Drop = new CreateTaxiDropDto { On = true, SameAsPickup = false, CityKey = 2738820 };
        var s = TaxiTripSnapshot.Build(Plan(), Start, "Trip", body, "Kathgodam", "Ramnagar");
        Assert.Equal(new TaxiSnapshotDrop("Ramnagar", false, Start.AddDays(3)), s.Drop);
    }

    [Fact]
    public void Json_round_trips()
    {
        var s = TaxiTripSnapshot.Build(Plan(), Start, "Uttarakhand Hills Trip", Body(), "Kathgodam", null);
        var back = TaxiTripSnapshot.FromJson(s.ToJson())!;
        Assert.Equal(s.Name, back.Name);
        Assert.Equal(s.Cities, back.Cities);
        Assert.Equal(s.Days.Select(d => (d.Day, d.Date, d.City, d.Taxi, d.Km, d.Minutes, string.Join("|", d.Places))),
            back.Days.Select(d => (d.Day, d.Date, d.City, d.Taxi, d.Km, d.Minutes, string.Join("|", d.Places))));
        Assert.Null(TaxiTripSnapshot.FromJson("not json"));
    }

    [Fact]
    public void Ring_snapshot_has_no_trip_name()
    {
        var s = TaxiTripSnapshot.Build(Plan(), Start, "Uttarakhand Hills Trip", Body(), "Kathgodam", null);
        var ring = s.ToRing(new TaxiDateRange(Start, Start.AddDays(3)), true, true, true, 3, 4, 2);
        Assert.Equal("Kathgodam to Nainital, Ranikhet", TaxiRingPayload.RouteLine(ring.PickupName, ring.Cities));
        Assert.DoesNotContain("Uttarakhand Hills Trip", ring.ToString());
    }
}
