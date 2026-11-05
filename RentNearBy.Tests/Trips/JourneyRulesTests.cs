namespace RentNearBy.Tests.Trips;

public class JourneyRulesTests
{
    private static TripEnquiry E(string status, int? driverFare = null, int? agreed = null) =>
        new() { Status = status, DriverFare = driverFare, AgreedFare = agreed };

    [Theory]
    [InlineData(TripEnquiryStatuses.FareSent)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth)]
    public void Answered_leads_are_free(string status) =>
        Assert.Equal(JourneyLegStatuses.Free, JourneyRules.LegStatusOf(E(status, 5000)));

    [Theory]
    [InlineData(TripEnquiryStatuses.NotAvailable)]
    [InlineData(TripEnquiryStatuses.Failed)]
    public void Denied_leads_are_not_free(string status) =>
        Assert.Equal(JourneyLegStatuses.NotFree, JourneyRules.LegStatusOf(E(status)));

    [Fact]
    public void Asked_is_no_answer() =>
        Assert.Equal(JourneyLegStatuses.NoAnswer, JourneyRules.LegStatusOf(E(TripEnquiryStatuses.Asked)));

    [Theory]
    [InlineData(TripEnquiryStatuses.Expired)]
    [InlineData(TripEnquiryStatuses.Closed)]
    [InlineData(TripEnquiryStatuses.CancelledByTraveller)]
    [InlineData(TripEnquiryStatuses.CancelledByOwner)]
    public void Ended_leads_are_unknown(string status) =>
        Assert.Equal(JourneyLegStatuses.Unknown, JourneyRules.LegStatusOf(E(status)));

    [Fact]
    public void Limits_match_the_contract()
    {
        Assert.Equal(20, JourneyRules.MaxLive);
        Assert.Equal(3, JourneyRules.MaxLegs);
        Assert.Equal(2, JourneyRules.Backups);
        Assert.Equal(30, JourneyRules.RefreshPerWindow);
    }

    [Fact]
    public void Dedupe_keys_fit_the_push_log_column()
    {
        var j = Guid.NewGuid();
        var t = Guid.NewGuid();
        Assert.True(JourneyRules.PausedKey(j, t).Length <= 80);
        Assert.True(JourneyRules.BackKey(j, t, new DateOnly(2026, 11, 1)).Length <= 80);
        Assert.True(JourneyRules.EveKey(j).Length <= 80);
    }

    [Fact]
    public void Dedupe_keys_are_distinct_per_event()
    {
        var j = Guid.NewGuid();
        var t = Guid.NewGuid();
        Assert.NotEqual(JourneyRules.PausedKey(j, t), JourneyRules.BackKey(j, t, new DateOnly(2026, 11, 1)));
        Assert.NotEqual(JourneyRules.BackKey(j, t, new DateOnly(2026, 11, 1)), JourneyRules.BackKey(j, t, new DateOnly(2026, 11, 2)));
        Assert.Equal(JourneyRules.PausedKey(j, t), JourneyRules.PausedKey(j, t));
    }

    // ---- journeys in a trip (CONTRACT_MCB D8)

    [Fact]
    public void Paused_and_back_route_to_my_journeys_without_a_trip()
    {
        var journey = Guid.NewGuid();
        var paused = JourneyNotifications.DriverPaused(Guid.NewGuid(), journey, "Sunil", "Haridwar", "Badrinath", "Mohan");
        var back = JourneyNotifications.DriverBack(Guid.NewGuid(), journey, "Sunil", "Haridwar", "Badrinath");

        Assert.Equal(JourneyNotifications.Route, paused.ActionRoute);
        Assert.Equal(JourneyNotifications.Route, back.ActionRoute);
        Assert.Equal($"{{\"journeyId\":\"{journey}\"}}", paused.ActionArgumentsJson);
    }

    [Fact]
    public void Paused_and_back_route_to_the_trip_plan_for_a_journey_in_a_trip()
    {
        var journey = Guid.NewGuid();
        var trip = Guid.NewGuid();
        var paused = JourneyNotifications.DriverPaused(Guid.NewGuid(), journey, "Sunil", "Haridwar", "Badrinath", null, trip);
        var back = JourneyNotifications.DriverBack(Guid.NewGuid(), journey, "Sunil", "Haridwar", "Badrinath", trip);

        Assert.Equal(UserNotificationRoutes.TripPlan, paused.ActionRoute);
        Assert.Equal(UserNotificationRoutes.TripPlan, back.ActionRoute);
        Assert.Contains($"\"tripId\":\"{trip}\"", paused.ActionArgumentsJson);
        Assert.Contains($"\"tripId\":\"{trip}\"", back.ActionArgumentsJson);
    }

    [Fact]
    public void Eve_key_is_unchanged()
    {
        var j = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("jrn:e:11111111222233334444555555555555", JourneyRules.EveKey(j));
    }

    private static SavedJourney WatchedJourney(DateOnly date, Guid? tripId, Guid driver) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        TripId = tripId,
        FromName = "Haridwar",
        ToName = "Badrinath",
        TravelDate = date,
        Legs = { new SavedJourneyLeg { LegIndex = 1, FromName = "Haridwar", ToName = "Badrinath", DriverTaxiId = driver, DriverAvailable = true } },
    };

    private static JourneyTaxiInfo Taxi(Guid id, bool isPublic) =>
        new(id, Guid.NewGuid(), "Sunil", "Innova", 7, null, null, "new", null, null, isPublic);

    [Fact]
    public void Watch_skips_the_eve_push_only_for_a_segment_journey()
    {
        var today = new DateOnly(2026, 11, 5);
        var driver = Guid.NewGuid();
        var taxis = new Dictionary<Guid, JourneyTaxiInfo> { [driver] = Taxi(driver, true) };

        var free = new List<RentNearBy.Infrastructure.Services.JourneyWatchService.Pending>();
        RentNearBy.Infrastructure.Services.JourneyWatchService.Evaluate(
            WatchedJourney(today.AddDays(1), null, driver), taxis, today, DateTime.UtcNow, free);
        var step = new List<RentNearBy.Infrastructure.Services.JourneyWatchService.Pending>();
        RentNearBy.Infrastructure.Services.JourneyWatchService.Evaluate(
            WatchedJourney(today.AddDays(1), Guid.NewGuid(), driver), taxis, today, DateTime.UtcNow, step, new HashSet<Guid>());
        var segmentJourney = WatchedJourney(today.AddDays(1), Guid.NewGuid(), driver);
        var segment = new List<RentNearBy.Infrastructure.Services.JourneyWatchService.Pending>();
        RentNearBy.Infrastructure.Services.JourneyWatchService.Evaluate(
            segmentJourney, taxis, today, DateTime.UtcNow, segment, new HashSet<Guid> { segmentJourney.Id });

        Assert.Single(free);
        Assert.Equal(JourneyRules.EveKey(Assert.Single(step).JourneyId), step[0].Dedupe);
        Assert.Empty(segment);
    }

    [Fact]
    public void Watch_routes_a_paused_driver_of_a_trip_journey_to_the_plan()
    {
        var today = new DateOnly(2026, 11, 5);
        var driver = Guid.NewGuid();
        var trip = Guid.NewGuid();
        var taxis = new Dictionary<Guid, JourneyTaxiInfo> { [driver] = Taxi(driver, false) };
        var pushes = new List<RentNearBy.Infrastructure.Services.JourneyWatchService.Pending>();

        var dirty = RentNearBy.Infrastructure.Services.JourneyWatchService.Evaluate(
            WatchedJourney(today.AddDays(5), trip, driver), taxis, today, DateTime.UtcNow, pushes);

        Assert.True(dirty);
        var push = Assert.Single(pushes);
        Assert.Equal(UserNotificationRoutes.TripPlan, push.Event.ActionRoute);
        Assert.Contains(trip.ToString(), push.Event.ActionArgumentsJson);
    }
}
