using System.Text;
using System.Text.Json;

namespace RentNearBy.Tests.TripCompaction;

// Trip compaction (Buddy_resume/trip_compaction/SPEC.md): the pure summary builder. Plan versions, malformed and empty plans,
// the 6 spot cap, stays per city, omitted pieces and the 2 KB bound.
public class TripArchiveSummaryBuilderTests
{
    private static readonly DateOnly Start = new(2026, 5, 10);

    private static TripSpot Spot(string name) => new() { Id = "sp-" + name, Name = name, Latitude = 29.3, Longitude = 79.4, VisitMinutes = 60 };

    private static TripPlan TwoCityPlan()
    {
        var plan = new TripPlan
        {
            Version = 7,
            Settings = new TripSettings { StartDate = Start },
            Legs =
            [
                new TripCityLeg { Index = 1, CityKey = 210000101, CityName = "Nainital", Latitude = 29.39, Longitude = 79.45 },
                new TripCityLeg { Index = 2, CityKey = 210000102, CityName = "Mukteshwar", Latitude = 29.47, Longitude = 79.65 },
            ],
            Days =
            [
                new TripDay { Day = 1, Leg = 1, Spots = [Spot("Naini Lake"), Spot("Snow View Point"), Spot("Mall Road")] },
                new TripDay { Day = 2, Leg = 1, Spots = [Spot("Tiffin Top"), Spot("Eco Cave Gardens"), Spot("Naina Devi Temple"), Spot("Land's End"), Spot("Naini Lake")] },
                new TripDay { Day = 3, Leg = 2, Spots = [Spot("Chauli ki Jali")] },
            ],
        };
        return plan;
    }

    private static Trip TripOf(string planJson, int days = 3, int spots = 9, string title = "Nainital & Mukteshwar") => new()
    {
        Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Title = title, Status = TripStatuses.Completed, CityKey = 210000101,
        CityName = "Nainital", CityLatitude = 29.39, CityLongitude = 79.45, PlanJson = planJson, DayCount = days, SpotCount = spots,
        StartDate = Start, EndDate = Start.AddDays(days - 1),
    };

    private static string Json(TripPlan plan) => JsonSerializer.Serialize(plan, TripJson.Options);

    private static TripEnquiry Stay(string name, string? city, DateOnly? checkIn = null) => new()
    {
        Id = Guid.NewGuid(), Kind = TripEnquiryKinds.Stay, Status = TripEnquiryStatuses.ConfirmedByBoth, ListingName = name,
        ContextJson = city == null ? "{}" : JsonSerializer.Serialize(new { city }), CheckIn = checkIn ?? Start, CheckOut = (checkIn ?? Start).AddDays(1),
        CreatedAt = DateTime.UtcNow,
    };

    private static TripArchiveSummary Build(Trip trip, IReadOnlyList<TripEnquiry>? stays = null, bool taxi = false, int members = 0, long? expenses = null)
        => TripArchiveSummaryBuilder.Build(new TripArchiveInput(trip, stays ?? [], taxi, members, expenses));

    [Fact]
    public void V7_two_city_plan_gives_cities_in_order_with_nights_spots_and_stays()
    {
        var s = Build(TripOf(Json(TwoCityPlan())), [Stay("Lake Inn", "Nainital"), Stay("Hill Nest", "Mukteshwar", Start.AddDays(2))], taxi: true, members: 2, expenses: 12450);

        Assert.Equal(TripArchive.SummaryVersion, s.Version);
        Assert.Equal("Nainital & Mukteshwar", s.Title);
        Assert.Equal(Start, s.StartDate);
        Assert.Equal(Start.AddDays(2), s.EndDate);
        Assert.Equal(3, s.DayCount);
        Assert.Equal(9, s.SpotCount);
        Assert.Equal(["Nainital", "Mukteshwar"], s.Cities.Select(c => c.Name));
        Assert.Equal(2, s.Cities[0].Nights);
        Assert.Equal(1, s.Cities[1].Nights);
        // Naini Lake twice counts once; capped at 6 names, the real count rides along for "+N more".
        Assert.Equal(TripArchive.MaxSpotsPerCity, s.Cities[0].Spots.Count);
        Assert.Equal(7, s.Cities[0].SpotCount);
        Assert.Equal(["Naini Lake", "Snow View Point", "Mall Road", "Tiffin Top", "Eco Cave Gardens", "Naina Devi Temple"], s.Cities[0].Spots);
        Assert.Equal(["Chauli ki Jali"], s.Cities[1].Spots);
        Assert.Equal(["Lake Inn"], s.Cities[0].Stays);
        Assert.Equal(["Hill Nest"], s.Cities[1].Stays);
        Assert.True(s.TaxiBooked);
        Assert.Equal(3, s.MemberCount);
        Assert.Equal(12450, s.ExpensesTotal);
    }

    [Fact]
    public void A_stay_without_a_city_falls_back_to_the_leg_of_its_check_in_day()
    {
        var s = Build(TripOf(Json(TwoCityPlan())), [Stay("Orchard Stay", null, Start.AddDays(2))]);
        Assert.Empty(s.Cities[0].Stays);
        Assert.Equal(["Orchard Stay"], s.Cities[1].Stays);
    }

    [Fact]
    public void V6_document_without_a_guide_reads_the_same()
    {
        var plan = TwoCityPlan();
        plan.Version = 6;
        var s = Build(TripOf(Json(plan)));
        Assert.Equal(["Nainital", "Mukteshwar"], s.Cities.Select(c => c.Name));
        Assert.Equal(2, s.Cities[0].Nights);
    }

    [Fact]
    public void Old_document_without_legs_or_day_legs_is_one_city_from_the_row()
    {
        const string v3 = """{"version":3,"cityKey":210000101,"cityName":"Nainital","days":[{"day":1,"spots":[{"id":"a","name":"Naini Lake"}]},{"day":2,"spots":[]}]}""";
        var s = Build(TripOf(v3, days: 2, spots: 1));
        var city = Assert.Single(s.Cities);
        Assert.Equal("Nainital", city.Name);
        Assert.Equal(2, city.Nights);
        Assert.Equal(["Naini Lake"], city.Spots);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"legs":[null,{"cityName":null}],"days":[null,{"day":1,"spots":null}]}""")]
    public void Malformed_or_empty_plan_never_throws_and_keeps_the_row_facts(string planJson)
    {
        var s = Build(TripOf(planJson, days: 0, spots: 0));
        Assert.Equal("Nainital & Mukteshwar", s.Title);
        Assert.Null(s.DayCount);
        Assert.Null(s.SpotCount);
        Assert.All(s.Cities, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
        Assert.Contains(s.Cities, c => c.Name == "Nainital");
        Assert.True(TripArchive.Bytes(s) < TripArchive.MaxBytes);
    }

    [Fact]
    public void Missing_pieces_are_omitted_from_the_json()
    {
        var json = TripArchive.Serialize(Build(TripOf(Json(TwoCityPlan()))));
        Assert.DoesNotContain("taxiBooked", json);
        Assert.DoesNotContain("expensesTotal", json);
        Assert.Contains("\"memberCount\":1", json);
        Assert.Contains("\"startDate\":\"2026-05-10\"", json);
        Assert.Contains("\"version\":1", json);
    }

    [Fact]
    public void Zero_expenses_total_is_omitted()
        => Assert.Null(Build(TripOf(Json(TwoCityPlan())), expenses: 0).ExpensesTotal);

    [Fact]
    public void A_huge_plan_still_fits_under_two_kilobytes()
    {
        var longName = new string('x', 200);
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start },
            Legs = Enumerable.Range(1, 12).Select(i => new TripCityLeg { Index = i, CityKey = 210000100 + i, CityName = $"{longName}{i}" }).ToList(),
            Days = Enumerable.Range(1, 12).Select(i => new TripDay
            {
                Day = i, Leg = i, Spots = Enumerable.Range(1, 12).Select(k => Spot($"{longName}-{i}-{k}")).ToList(),
            }).ToList(),
        };
        var stays = Enumerable.Range(1, 12).SelectMany(i => Enumerable.Range(1, 5).Select(k => Stay($"{longName} stay {i}-{k}", $"{longName}{i}"))).ToList();

        var s = Build(TripOf(Json(plan), days: 12, spots: 144, title: new string('T', 80)), stays, taxi: true, members: 9, expenses: 999999);

        Assert.True(TripArchive.Bytes(s) <= TripArchive.MaxBytes, $"{TripArchive.Bytes(s)} bytes");
        Assert.NotEmpty(s.Cities);
        Assert.All(s.Cities, c => Assert.True(c.Name.Length <= TripArchive.NameMax));
        Assert.All(s.Cities, c => Assert.True(c.Spots.Count <= TripArchive.MaxSpotsPerCity));
        Assert.All(s.Cities, c => Assert.True(c.Stays.Count <= TripArchive.MaxStaysPerCity));
        Assert.True(s.Cities.Count <= TripArchive.MaxCities);
    }

    [Fact]
    public void Non_ascii_names_count_in_utf8_bytes_for_the_bound()
    {
        var plan = TwoCityPlan();
        foreach (var d in plan.Days) d.Spots = Enumerable.Range(1, 30).Select(i => Spot(new string('न', 55) + i)).ToList();
        var s = Build(TripOf(Json(plan)));
        Assert.True(Encoding.UTF8.GetByteCount(TripArchive.Serialize(s)) <= TripArchive.MaxBytes);
    }

    [Fact]
    public void Read_round_trips_and_is_tolerant()
    {
        var built = Build(TripOf(Json(TwoCityPlan())), [Stay("Lake Inn", "Nainital")], taxi: true, members: 1, expenses: 500);
        var read = TripArchive.Read(TripArchive.Serialize(built))!;
        Assert.Equal(built.Cities.Select(c => c.Name), read.Cities.Select(c => c.Name));
        Assert.Equal(500, read.ExpensesTotal);
        Assert.True(read.TaxiBooked);

        Assert.Null(TripArchive.Read(null));
        Assert.Null(TripArchive.Read(""));
        Assert.Null(TripArchive.Read("garbage"));
        Assert.Null(TripArchive.Read("[]"));
        var messy = TripArchive.Read("""{"version":1,"cities":[null,{"name":""},{"name":"Ooty","nights":-2,"spots":["a","","b","c","d","e","f","g"],"stays":null}]}""")!;
        var ooty = Assert.Single(messy.Cities);
        Assert.Equal(0, ooty.Nights);
        Assert.Equal(TripArchive.MaxSpotsPerCity, ooty.Spots.Count);
        Assert.Empty(ooty.Stays);
    }

    [Fact]
    public void Leg_cities_come_from_the_summary_only_for_an_archived_trip()
    {
        var plan = TwoCityPlan();
        plan.Normalize(210000101, "Nainital", 29.39, 79.45);
        var live = TripOf(Json(plan));
        Assert.Equal(["Nainital", "Mukteshwar"], TripArchive.LegCities(live, plan, null));

        var archived = TripOf(TripArchive.EmptyPlanJson);
        archived.ArchivedAt = DateTime.UtcNow;
        var empty = TripPlanReader.Read(archived);
        var summary = new TripArchiveSummary { Cities = [new TripArchiveCity { Name = "Ooty" }, new TripArchiveCity { Name = "Coonoor" }] };
        Assert.Equal(["Ooty", "Coonoor"], TripArchive.LegCities(archived, empty, summary));
        // A missing or empty summary never gives an empty route line.
        Assert.Equal(["Nainital"], TripArchive.LegCities(archived, empty, null));
        Assert.Equal(["Nainital"], TripArchive.LegCities(archived, empty, new TripArchiveSummary()));
    }
}
