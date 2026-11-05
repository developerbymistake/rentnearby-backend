using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Tests.Trips;

public class TripRound2FixJbTests
{
    private static readonly Guid Traveller = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000021");
    private static readonly Guid Owner = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000022");
    private static readonly DateOnly Start = new(2026, 11, 5);
    private static readonly DateTime Now = new(2026, 11, 4, 6, 0, 0, DateTimeKind.Utc);

    private static DateOnly Night(int day) => Start.AddDays(day - 1);

    private static string Context(params int[] days) => JsonSerializer.Serialize(new TripEnquiryContext
    {
        City = "Mussoorie",
        Adults = 2,
        Kids = 0,
        Days = days.Select(d => new TripEnquiryContextDay { Day = d, Date = Night(d), Spots = [$"Spot {d}"] }).ToList(),
    }, TripJson.Options);

    private static TripEnquiry Booked(int firstDay, int nights, int total = 9000) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        TravellerUserId = Traveller,
        OwnerUserId = Owner,
        Kind = TripEnquiryKinds.Stay,
        ListingId = Guid.NewGuid(),
        ListingName = "Deodar Homestay",
        Day = (short)firstDay,
        CheckIn = Night(firstDay),
        CheckOut = Night(firstDay + nights),
        Status = TripEnquiryStatuses.ConfirmedByBoth,
        AgreedTotal = total,
        AcceptedOfferId = Guid.NewGuid(),
        AdvanceStatus = TripAdvanceStatuses.Received,
        ContextJson = Context(Enumerable.Range(firstDay, nights).ToArray()),
    };

    private static List<int> Days(TripEnquiry e) => TripEnquiryDtoBuilder.ReadContext(e.ContextJson).Days.Select(d => d.Day).ToList();

    // Finding 1: routes tagged SkipTripPlannerGate pass the planner kill switch; untagged routes are still blocked.
    private static async Task<object?> RunGate(bool skip)
    {
        var http = new DefaultHttpContext();
        var metadata = skip ? new EndpointMetadataCollection(new SkipTripPlannerGateAttribute()) : new EndpointMetadataCollection();
        http.SetEndpoint(new Endpoint(null, metadata, "GET /trips/leads/driver"));
        var filter = new TripPlannerGateFilter(Options.Create(new TripPlannerOptions { Enabled = false }));
        return await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>("next"));
    }

    [Fact]
    public async Task Planner_off_tagged_route_passes()
        => Assert.Equal("next", await RunGate(skip: true));

    [Fact]
    public async Task Planner_off_untagged_route_blocked()
        => Assert.NotEqual("next", await RunGate(skip: false));

    // Finding 6: replacing the last night of a 3-night booking keeps nights 1-2 at their share.
    [Fact]
    public void Replace_last_night_keeps_earlier_nights()
    {
        var r = Booked(1, 3);
        var plan = TripReplaceSplit.Plan(r, Night(3), Night(4), Traveller, Now);

        Assert.Same(r, plan.Kept);
        Assert.Null(plan.KeptAfter);
        Assert.Equal(TripEnquiryStatuses.ConfirmedByBoth, r.Status);
        Assert.Equal(Night(1), r.CheckIn);
        Assert.Equal(Night(3), r.CheckOut);
        Assert.Equal((short)1, r.Day);
        Assert.Equal(6000, r.AgreedTotal);
        Assert.Equal(new[] { 1, 2 }, Days(r));
        Assert.Equal(TripAdvanceStatuses.Received, r.AdvanceStatus);

        var c = plan.Cancelled;
        Assert.NotEqual(r.Id, c.Id);
        Assert.Equal(TripEnquiryStatuses.CancelledByTraveller, c.Status);
        Assert.Equal(Traveller, c.CancelledByUserId);
        Assert.Equal(Night(3), c.CheckIn);
        Assert.Equal(Night(4), c.CheckOut);
        Assert.Equal((short)3, c.Day);
        Assert.Equal(3000, c.AgreedTotal);
        Assert.Equal(new[] { 3 }, Days(c));
        Assert.Equal(TripAdvanceStatuses.None, c.AdvanceStatus);
    }

    [Fact]
    public void Replace_middle_night_splits_before_and_after()
    {
        var r = Booked(1, 3, total: 10000);
        var plan = TripReplaceSplit.Plan(r, Night(2), Night(3), Traveller, Now);

        var after = Assert.IsType<TripEnquiry>(plan.KeptAfter);
        Assert.Equal(Night(2), r.CheckOut);
        Assert.Equal(Night(3), after.CheckIn);
        Assert.Equal(Night(4), after.CheckOut);
        Assert.Equal((short)3, after.Day);
        Assert.Equal(TripEnquiryStatuses.ConfirmedByBoth, after.Status);
        Assert.Equal(Night(2), plan.Cancelled.CheckIn);
        Assert.Equal(10000, r.AgreedTotal!.Value + after.AgreedTotal!.Value + plan.Cancelled.AgreedTotal!.Value);
        Assert.Equal(new[] { 3 }, Days(after));
    }

    [Fact]
    public void Replace_first_night_moves_check_in_and_day()
    {
        var r = Booked(2, 3);
        var plan = TripReplaceSplit.Plan(r, Night(1), Night(3), Traveller, Now);

        Assert.Same(r, plan.Kept);
        Assert.Null(plan.KeptAfter);
        Assert.Equal(Night(3), r.CheckIn);
        Assert.Equal(Night(5), r.CheckOut);
        Assert.Equal((short)3, r.Day);
        Assert.Equal(6000, r.AgreedTotal);
        Assert.Equal(Night(2), plan.Cancelled.CheckIn);
        Assert.Equal(Night(3), plan.Cancelled.CheckOut);
    }

    [Fact]
    public void Replace_covering_every_night_cancels_whole_row()
    {
        var r = Booked(2, 1);
        var plan = TripReplaceSplit.Plan(r, Night(1), Night(4), Traveller, Now);

        Assert.Null(plan.Kept);
        Assert.Same(r, plan.Cancelled);
        Assert.Equal(TripEnquiryStatuses.CancelledByTraveller, r.Status);
        Assert.Equal(Night(2), r.CheckIn);
    }

    [Fact]
    public void Replace_shrinks_phone_booking_nights()
    {
        var r = Booked(1, 2, total: 4000);
        r.AcceptedOfferId = null;
        r.PhoneBookingJson = JsonSerializer.Serialize(new TripPhoneBooking { HostName = "Ramesh", Nights = 2, AgreedTotal = 4000 }, TripJson.Options);

        TripReplaceSplit.Plan(r, Night(2), Night(3), Traveller, Now);

        var booking = TripEnquiryDtoBuilder.ReadPhoneBooking(r.PhoneBookingJson);
        Assert.NotNull(booking);
        Assert.Equal(1, booking.Nights);
        Assert.Equal(2000, booking.AgreedTotal);
    }

    [Theory]
    [InlineData(1, 1, "the night of 05 Nov")]
    [InlineData(1, 3, "3 nights from 05 Nov")]
    public void Nights_text(int day, int nights, string expected)
        => Assert.Equal(expected, TripReplaceSplit.NightsText(Night(day), Night(day + nights)));

    // Finding 5: an accepted change keeps the known days, adds the new nights from the plan and rewrites the text.
    [Fact]
    public void Change_rebuilds_context_days_and_text()
    {
        var e = Booked(1, 1);
        var plan = new TripPlan
        {
            Days =
            [
                new TripDay { Day = 1, Spots = [new TripSpot { Name = "Spot 1", VisitMinutes = 60 }] },
                new TripDay { Day = 2, Spots = [new TripSpot { Name = "Kempty Falls", VisitMinutes = 90 }] },
            ],
        };

        TripEnquiryHandlers.RebuildStayContext(e, TripEnquiryDtoBuilder.ReadContext(e.ContextJson), plan, Start, Night(1), Night(3));

        var ctx = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
        Assert.Equal(new[] { 1, 2 }, ctx.Days.Select(d => d.Day));
        Assert.Equal(new[] { "Kempty Falls" }, ctx.Days[1].Spots);
        Assert.Equal(90, ctx.Days[1].Minutes);
        Assert.Contains("(2 nights)", ctx.Text);
        Assert.Contains("Kempty Falls", ctx.Text);
    }

    // Finding 3: a role change clears the leaving kind's trip owner inbox rows too.
    [Fact]
    public void Role_change_purges_trip_owner_routes()
    {
        Assert.Contains(UserNotificationRoutes.StayEnquiries, UserNotificationRoutes.OwnedByKind(ListingKinds.Stay));
        Assert.Contains(UserNotificationRoutes.TripLeads, UserNotificationRoutes.OwnedByKind(ListingKinds.Taxi));
        Assert.DoesNotContain(UserNotificationRoutes.TripLeads, UserNotificationRoutes.OwnedByKind(ListingKinds.Stay));
    }
}
