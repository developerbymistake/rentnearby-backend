namespace RentNearBy.Tests.Trips;

// CONTRACT_MCD D3: Haridwar day 1, Badrinath days 2-4, day 2 = travel day (02 Nov).
public class TripTravelEveReminderTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateOnly TravelDate = new(2026, 11, 2);
    private static readonly Guid TripId = Guid.Parse("cccccccc-0000-0000-0000-000000000004");
    private static readonly Guid SunilTaxi = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid MohanTaxi = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private static TripPlan Plan(TripTravel? arrival, string from = "Haridwar", string to = "Badrinath")
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start },
            Legs =
            [
                new TripCityLeg { Index = 1, CityName = from },
                new TripCityLeg { Index = 2, CityName = to, Arrival = arrival },
            ],
        };
        for (var d = 1; d <= 4; d++) plan.Days.Add(new TripDay { Day = d, Leg = d == 1 ? 1 : 2 });
        return plan;
    }

    private static SavedJourney Journey(int cars = 2, DateOnly? date = null)
    {
        var j = new SavedJourney { Id = Guid.NewGuid(), TripId = TripId, TravelDate = date ?? TravelDate, Cars = (short)cars, People = 4 };
        var taxis = new[] { SunilTaxi, MohanTaxi, Guid.NewGuid() };
        for (var i = 1; i <= cars; i++)
            j.Legs.Add(new SavedJourneyLeg { Id = Guid.NewGuid(), JourneyId = j.Id, LegIndex = (short)i, DriverTaxiId = taxis[i - 1] });
        return j;
    }

    private static TripTravel Arrival(SavedJourney j) =>
        new() { FromName = "Haridwar", JourneyId = j.Id, Cars = j.Legs.Count };

    private static TripEnquiry Row(Guid journeyId, int car, string status, string? pickup = null) => new()
    {
        Id = Guid.NewGuid(),
        TripId = TripId,
        Kind = TripEnquiryKinds.Taxi,
        Day = 2,
        DaysJson = "[2]",
        Status = status,
        CheckIn = TravelDate,
        CheckOut = TravelDate.AddDays(1),
        PickupTime = pickup,
        ContextJson = $$"""{"city":"Badrinath","journeyId":"{{journeyId}}","legIndex":{{car}}}""",
    };

    private static JourneyTaxiInfo Taxi(Guid id, string name) =>
        new(id, Guid.NewGuid(), name, "Sedan", 4, null, "Haridwar", "new", "9876543210", null, true);

    private static readonly IReadOnlyDictionary<Guid, JourneyTaxiInfo> Taxis = new Dictionary<Guid, JourneyTaxiInfo>
    {
        [SunilTaxi] = Taxi(SunilTaxi, "Sunil Kumar"),
        [MohanTaxi] = Taxi(MohanTaxi, "Mohan Bisht"),
    };

    private static readonly IReadOnlyDictionary<Guid, JourneyTaxiInfo> NoTaxis = new Dictionary<Guid, JourneyTaxiInfo>();

    private static string Body(TripPlan plan, SavedJourney? journey, params TripEnquiry[] rows)
        => TripTravelEveReminder.Build(plan, 2, TravelDate, rows, journey, Taxis).Body;

    [Fact]
    public void Title_names_both_cities()
        => Assert.Equal("Tomorrow: Haridwar → Badrinath", TripTravelEveReminder.Build(Plan(null), 2, TravelDate, [], null, NoTaxis).Title);

    [Fact]
    public void Long_title_is_cut_to_60()
    {
        var title = TripTravelEveReminder.Build(Plan(null, new string('a', 40), new string('b', 40)), 2, TravelDate, [], null, NoTaxis).Title;
        Assert.True(title.Length <= TripTravelEveReminder.TitleMax);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void No_arrival_says_not_planned()
        => Assert.Equal("No travel planned yet. Open the trip.", Body(Plan(null), null));

    [Fact]
    public void Own_travel_mark_says_you_will_arrange_it()
    {
        var plan = Plan(null);
        plan.Legs![1].TravelMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
        Assert.Equal("You'll arrange your travel. Open the trip.", Body(plan, null));
    }

    [Fact]
    public void Journey_with_no_booked_car()
    {
        var j = Journey();
        Assert.Equal("No driver booked yet. Open the trip.", Body(Plan(Arrival(j)), j, Row(j.Id, 1, TripEnquiryStatuses.Asked)));
    }

    [Fact]
    public void One_of_two_booked_with_pickup()
    {
        var j = Journey();
        Assert.Equal("Sunil Kumar 6:00 am. 1 car not booked yet.",
            Body(Plan(Arrival(j)), j, Row(j.Id, 1, TripEnquiryStatuses.ConfirmedByBoth, "06:00")));
    }

    [Fact]
    public void Both_booked_asks_to_tap_for_the_numbers()
    {
        var j = Journey();
        Assert.Equal("Sunil Kumar 6:00 am. Tap for the drivers' numbers.",
            Body(Plan(Arrival(j)), j,
                Row(j.Id, 1, TripEnquiryStatuses.ConfirmedByBoth, "06:00"), Row(j.Id, 2, TripEnquiryStatuses.BookedByTraveller)));
    }

    [Fact]
    public void First_booked_car_leads_and_unknown_driver_falls_back()
    {
        var j = Journey();
        Assert.Equal("Mohan Bisht. 1 car not booked yet.",
            Body(Plan(Arrival(j)), j, Row(j.Id, 2, TripEnquiryStatuses.BookedByTraveller)));
        Assert.Equal("Your driver. 1 car not booked yet.",
            TripTravelEveReminder.Build(Plan(Arrival(j)), 2, TravelDate, [Row(j.Id, 1, TripEnquiryStatuses.BookedByTraveller)], j, NoTaxis).Body);
    }

    [Fact]
    public void Date_changed_journey()
    {
        var j = Journey(date: TravelDate.AddDays(1));
        Assert.Equal("Travel date changed. Check your drivers.",
            Body(Plan(Arrival(j)), j, Row(j.Id, 1, TripEnquiryStatuses.ConfirmedByBoth, "06:00")));
    }

    [Fact]
    public void Missing_journey_in_journey_mode_reads_as_date_changed()
    {
        var j = Journey();
        Assert.Equal("Travel date changed. Check your drivers.", Body(Plan(Arrival(j)), null));
        j.IsDeleted = true;
        Assert.Equal("Travel date changed. Check your drivers.", Body(Plan(Arrival(j)), j));
    }

    [Fact]
    public void Stale_rows_of_another_journey_are_ignored()
    {
        var j = Journey();
        var old = Guid.NewGuid();
        Assert.Equal("No driver booked yet. Open the trip.",
            Body(Plan(Arrival(j)), j,
                Row(old, 1, TripEnquiryStatuses.ConfirmedByBoth, "06:00"), Row(old, 2, TripEnquiryStatuses.BookedByTraveller)));
    }

    [Fact]
    public void Cancelled_and_closed_rows_are_not_booked()
    {
        var j = Journey(cars: 1);
        Assert.Equal("No driver booked yet. Open the trip.",
            Body(Plan(Arrival(j)), j, Row(j.Id, 1, TripEnquiryStatuses.CancelledByTraveller), Row(j.Id, 1, TripEnquiryStatuses.Closed)));
    }
}
