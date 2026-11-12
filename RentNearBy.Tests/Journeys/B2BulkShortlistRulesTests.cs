using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Tests.Journeys;

// Fixer B2: the pure decisions behind bulk enquiry and shortlist (the database paths are in TripBulkEnquiryIntegrationTests).
public class B2BulkShortlistRulesTests
{
    private static readonly Guid ThisTrip = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);

    private static TripEnquiry Row(Guid? tripId) => new() { Id = Guid.NewGuid(), TripId = tripId, ListingId = Guid.NewGuid() };

    // ---- SB1: AlreadyOpen only for this trip's rows ----

    [Fact]
    public void Overlap_in_another_trip_is_open_in_another_trip_and_in_this_trip_already_open()
    {
        Assert.Equal(TripSkipReasons.OpenInAnotherTrip, TripBulkRules.OverlapSkipReason([Row(Guid.NewGuid())], ThisTrip));
        Assert.Equal(TripSkipReasons.OpenInAnotherTrip, TripBulkRules.OverlapSkipReason([Row(null)], ThisTrip));
        Assert.Equal(TripSkipReasons.AlreadyOpen, TripBulkRules.OverlapSkipReason([Row(ThisTrip)], ThisTrip));
        Assert.Equal(TripSkipReasons.AlreadyOpen, TripBulkRules.OverlapSkipReason([Row(Guid.NewGuid()), Row(ThisTrip)], ThisTrip));
    }

    [Fact]
    public void Skip_reason_maps_to_its_own_send_status()
    {
        Assert.Equal("OpenInAnotherTrip", TripSendStatuses.FromSkip(TripSkipReasons.OpenInAnotherTrip));
        Assert.Equal("AlreadyOpen", TripSendStatuses.FromSkip(TripSkipReasons.AlreadyOpen));
        Assert.Equal("NotAvailable", TripSendStatuses.FromSkip(TripSkipReasons.NotAvailable));
        Assert.Equal("Blocked", TripSendStatuses.FromSkip(TripSkipReasons.Blocked));
        Assert.Equal("LimitReached", TripSendStatuses.FromSkip(TripSkipReasons.LimitReached));
    }

    [Fact]
    public void A_skipped_listing_in_another_trip_has_no_enquiry_id_and_its_status()
    {
        var listing = Guid.NewGuid();
        var results = TripBulk.BuildResults(
            [listing], new HashSet<Guid>(), new List<Guid>(), [(listing, TripSkipReasons.OpenInAnotherTrip)], [], null);
        var only = Assert.Single(results);
        Assert.Equal(TripSendStatuses.OpenInAnotherTrip, only.Status);
        Assert.Null(only.EnquiryId);
        Assert.Equal(TripSkipReasons.OpenInAnotherTrip, only.Reason);
    }

    // ---- SB2: unavailable rows leave the shortlist ----

    [Theory]
    [InlineData(TripSkipReasons.NotAvailable, true)]
    [InlineData(TripSkipReasons.Blocked, true)]
    [InlineData(TripSkipReasons.AlreadyOpen, false)]
    [InlineData(TripSkipReasons.OpenInAnotherTrip, false)]
    [InlineData(TripSkipReasons.LimitReached, false)]
    public void Only_not_available_and_blocked_close_the_shortlist_row(string reason, bool closes)
        => Assert.Equal(closes, TripBulkRules.ClosesShortlistRow(reason));

    [Fact]
    public void Closed_reasons_fit_the_twelve_character_column_and_are_distinct()
    {
        string[] reasons =
        [
            TripShortlistClosedReasons.Unavailable, TripShortlistClosedReasons.Declined, TripShortlistClosedReasons.NoReply,
            TripShortlistClosedReasons.Removed, TripShortlistClosedReasons.Booked, TripShortlistClosedReasons.TripClosed,
            TripShortlistClosedReasons.Sent, TripShortlistClosedReasons.Own, TripShortlistClosedReasons.Skipped,
        ];
        Assert.All(reasons, r => Assert.True(r.Length <= 12, r));
        Assert.Equal(reasons.Length, reasons.Distinct().Count());
    }

    // ---- SB6: the step's three places ----

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 2, true)]
    [InlineData(1, 1, true)]
    [InlineData(3, 0, false)]
    [InlineData(2, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(0, 3, false)]
    public void Open_enquiries_and_active_rows_share_the_three_places(int open, int active, bool room)
        => Assert.Equal(room, TripBulkRules.StepHasRoom(open, active));

    [Fact]
    public void The_step_full_text_is_the_agreed_sentence()
        => Assert.Equal("You already have 3 open enquiries here. Wait for a reply or withdraw one.", TripBulkRules.StepFullMessage);

    // ---- SB10: host area, names ----

    [Fact]
    public void Host_must_be_in_the_leg_area_but_an_unknown_area_never_refuses()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.True(TripBulkRules.HostInLegArea(a, a));
        Assert.False(TripBulkRules.HostInLegArea(a, b));
        Assert.True(TripBulkRules.HostInLegArea(null, b));
        Assert.True(TripBulkRules.HostInLegArea(a, null));
        Assert.True(TripBulkRules.HostInLegArea(null, null));
    }

    [Fact]
    public void Leg_district_is_the_legs_own_in_a_multi_city_plan_and_the_trips_in_a_single_city_plan()
    {
        var home = Guid.NewGuid();
        var second = Guid.NewGuid();
        var trip = new Trip { DistrictId = home };
        var single = new TripPlan();
        for (var d = 1; d <= 2; d++) single.Days.Add(new TripDay { Day = d });
        Assert.Equal(home, TripEnquiryHandlers.LegDistrict(trip, single, 1));

        var multi = new TripPlan
        {
            Legs =
            [
                new TripCityLeg { Index = 1, CityName = "Nainital", DistrictId = home },
                new TripCityLeg { Index = 2, CityName = "Almora", DistrictId = second },
            ],
        };
        for (var d = 1; d <= 4; d++) multi.Days.Add(new TripDay { Day = d, Leg = d <= 2 ? 1 : 2 });
        Assert.Equal(home, TripEnquiryHandlers.LegDistrict(trip, multi, 2));
        Assert.Equal(second, TripEnquiryHandlers.LegDistrict(trip, multi, 3));
        multi.Legs![1].DistrictId = null;
        Assert.Null(TripEnquiryHandlers.LegDistrict(trip, multi, 4));
    }

    [Fact]
    public void Names_come_from_the_server_in_the_same_form_as_the_enquiry_rows()
    {
        Assert.Equal("Raju", TripBulk.TaxiLeadName("Raju", "Innova"));
        Assert.Equal("Innova", TripBulk.TaxiLeadName("  ", "Innova"));
        Assert.Equal("Taxi", TripBulk.TaxiLeadName(null, null));
        Assert.Equal("Raju · Innova", TripBulk.CarName("Raju", "Innova"));
        Assert.Equal("Raju", TripBulk.CarName("Raju", null));
        Assert.Equal("Taxi", TripBulk.CarName(null, " "));
        Assert.True(TripBulk.CarName(new string('a', 200), new string('b', 200)).Length <= 120);
    }

    // ---- RM-3 (l): the server rejects days of two cities, so a lead only ever holds days of one area ----

    [Fact]
    public void Days_of_two_cities_are_not_one_leg_so_a_lead_never_holds_them()
    {
        var plan = new TripPlan
        {
            Legs =
            [
                new TripCityLeg { Index = 1, CityName = "Nainital" },
                new TripCityLeg { Index = 2, CityName = "Almora" },
            ],
        };
        for (var d = 1; d <= 4; d++) plan.Days.Add(new TripDay { Day = d, Leg = d <= 2 ? 1 : 2 });
        Assert.True(TripLegRules.DaysInOneLeg(plan, [1, 2]));
        Assert.True(TripLegRules.DaysInOneLeg(plan, [3, 4]));
        Assert.False(TripLegRules.DaysInOneLeg(plan, [1, 2, 3, 4]));
        Assert.False(TripLegRules.DaysInOneLeg(plan, [2, 3]));
    }

    // ---- (h) the window ----

    [Fact]
    public void An_asked_enquiry_is_closed_the_moment_its_window_ends()
    {
        Assert.True(TripBulkRules.WindowEnded(TripEnquiryStatuses.Asked, Now, Now));
        Assert.True(TripBulkRules.WindowEnded(TripEnquiryStatuses.Asked, Now.AddSeconds(-1), Now));
        Assert.False(TripBulkRules.WindowEnded(TripEnquiryStatuses.Asked, Now.AddSeconds(1), Now));
        Assert.False(TripBulkRules.WindowEnded(TripEnquiryStatuses.Offered, Now.AddDays(-1), Now));
        Assert.False(TripBulkRules.WindowEnded(TripEnquiryStatuses.ConfirmedByBoth, Now.AddDays(-1), Now));
    }

    // ---- (i) offer replay ----

    private static TripOffer Offer(Guid sender, DateTime createdAt, string status = TripOfferStatuses.Pending) => new()
    {
        Kind = TripOfferKinds.Offer, Status = status, SenderUserId = sender, CheckIn = new DateOnly(2026, 11, 20),
        CheckOut = new DateOnly(2026, 11, 22), Total = 4000, LinesJson = "[{\"units\":1}]", CreatedAt = createdAt,
    };

    [Fact]
    public void The_same_offer_within_two_minutes_is_a_replay_and_anything_different_is_not()
    {
        var host = Guid.NewGuid();
        var checkIn = new DateOnly(2026, 11, 20);
        var checkOut = new DateOnly(2026, 11, 22);
        const string lines = "[{\"units\":1}]";
        var sent = Offer(host, Now.AddSeconds(-90));

        Assert.True(TripBulkRules.IsOfferReplay(sent, host, checkIn, checkOut, 4000, lines, Now));
        Assert.True(TripBulkRules.IsOfferReplay(Offer(host, Now.AddMinutes(-2)), host, checkIn, checkOut, 4000, lines, Now));
        Assert.False(TripBulkRules.IsOfferReplay(Offer(host, Now.AddMinutes(-3)), host, checkIn, checkOut, 4000, lines, Now));
        Assert.False(TripBulkRules.IsOfferReplay(sent, Guid.NewGuid(), checkIn, checkOut, 4000, lines, Now));
        Assert.False(TripBulkRules.IsOfferReplay(sent, host, checkIn, checkOut.AddDays(1), 4000, lines, Now));
        Assert.False(TripBulkRules.IsOfferReplay(sent, host, checkIn, checkOut, 4500, lines, Now));
        Assert.False(TripBulkRules.IsOfferReplay(sent, host, checkIn, checkOut, 4000, "[{\"units\":2}]", Now));
        Assert.False(TripBulkRules.IsOfferReplay(Offer(host, Now.AddSeconds(-10), TripOfferStatuses.Superseded), host, checkIn, checkOut, 4000, lines, Now));
        var counter = Offer(host, Now.AddSeconds(-10));
        counter.Kind = TripOfferKinds.Counter;
        Assert.False(TripBulkRules.IsOfferReplay(counter, host, checkIn, checkOut, 4000, lines, Now));
    }

    // ---- (g) outbox keys ----

    [Fact]
    public void Update_keys_are_unique_per_change_distinct_from_the_new_row_key_and_fit_the_column()
    {
        var id = Guid.NewGuid();
        var first = TripBulkRules.UpdateKey(id, "upd", Now);
        var second = TripBulkRules.UpdateKey(id, "upd", Now.AddSeconds(5));
        Assert.NotEqual(first, second);
        Assert.NotEqual($"enq:{id:N}:new", first);
        Assert.NotEqual(first, TripBulkRules.UpdateKey(id, "wd", Now));
        Assert.True(first.Length <= 120);
        Assert.Equal(first, TripBulkRules.UpdateKey(id, "upd", Now));
    }

    [Fact]
    public void The_trip_less_lock_key_is_per_journey_leg_and_driver()
    {
        var j = Guid.NewGuid();
        var t = Guid.NewGuid();
        Assert.Equal(TripBulkRules.JourneyLeadLockKey(j, 1, t), TripBulkRules.JourneyLeadLockKey(j, 1, t));
        Assert.NotEqual(TripBulkRules.JourneyLeadLockKey(j, 1, t), TripBulkRules.JourneyLeadLockKey(j, 2, t));
        Assert.NotEqual(TripBulkRules.JourneyLeadLockKey(j, 1, t), TripBulkRules.JourneyLeadLockKey(Guid.NewGuid(), 1, t));
    }

    // ---- (d) the neutral system line ----

    [Fact]
    public void Booked_elsewhere_line_is_neutral_for_stays_and_taxis()
    {
        Assert.Equal("The guest booked this elsewhere.", TripSystemLines.Text(TripSystemLines.GuestBookedElsewhere));
        Assert.Equal("The guest booked another stay for these dates.", TripSystemLines.Text(TripSystemLines.BookedElsewhere));
    }
}
