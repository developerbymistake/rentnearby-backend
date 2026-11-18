using System.Reflection;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// Every contract name exists exactly once as a constant, and the timing numbers equal contract section 12.
public class TaxiRequestConstantsTests
{
    private static IEnumerable<string> Consts(Type t)
        => t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!);

    [Theory]
    [InlineData(typeof(RequestStates), new[] { "Open", "Collecting", "Selected", "Booked", "InProgress", "Completed", "Cancelled", "Expired" })]
    [InlineData(typeof(RequestViews), new[] { "Finding", "FindingWaiting", "NoReply", "Collecting", "Selected", "Booked", "InProgress", "Rate", "Done", "Busy", "Ended" })]
    [InlineData(typeof(OfferStates), new[] { "Ringing", "Accepted", "Declined", "Missed", "Selected", "NotSelected", "NotAgreed", "Confirmed", "Withdrawn", "Closed" })]
    [InlineData(typeof(OfferBuckets), new[] { "New", "Booked", "Earlier" })]
    [InlineData(typeof(BookingStates), new[] { "Booked", "InProgress", "Completed", "Cancelled" })]
    [InlineData(typeof(BookingCancelledBy), new[] { "Traveller", "Driver", "Admin", "System" })]
    [InlineData(typeof(BookingCompletedBy), new[] { "Auto", "Traveller", "Driver" })]
    [InlineData(typeof(RequestEndReasons), new[] { "TravellerCancelled", "PickupChanged", "BookingCancelledByTraveller", "DriverLeftTrip", "AdminCancelled", "AccountClosed", "AllDriversBusy", "NotBooked" })]
    [InlineData(typeof(OfferEndReasons), new[] { "Expired", "Filled", "ConfirmTimeout", "DeclinedBooking", "RequestCancelled", "RequestExpired", "Booked", "DriverOffline", "ListingRemoved" })]
    [InlineData(typeof(ReasonChips), new[] { "ChangedPlan", "NotReachable", "PriceNotAgreed", "VehicleIssue", "Other" })]
    [InlineData(typeof(PlanLockReasons), new[] { "TaxiRequestActive", "TaxiBookingActive" })]
    [InlineData(typeof(DueActionKinds), new[] { "OfferExpire", "ConfirmExpire", "RequestWaitExpire", "RequestStaleExpire", "BookingStart", "BookingAutoComplete" })]
    [InlineData(typeof(DueActionStates), new[] { "Pending", "Running", "Done", "Cancelled", "Skipped", "Failed" })]
    public void Constants_are_exactly_the_contract_names_once_and_All_lists_them(Type holder, string[] contract)
    {
        var consts = Consts(holder).ToArray();
        Assert.Equal(contract.OrderBy(x => x), consts.OrderBy(x => x));
        Assert.Equal(consts.Length, consts.Distinct().Count());
        var all = (IReadOnlyList<string>)holder.GetField("All")!.GetValue(null)!;
        Assert.Equal(contract.OrderBy(x => x), all.OrderBy(x => x));
        var sql = (string)holder.GetField("SqlList")!.GetValue(null)!;
        Assert.Equal(string.Join(", ", all.Select(v => $"'{v}'")), sql);
    }

    [Fact]
    public void Active_request_states_are_the_first_five()
    {
        Assert.Equal(["Open", "Collecting", "Selected", "Booked", "InProgress"], RequestStates.Active);
        Assert.Equal("'Open', 'Collecting', 'Selected', 'Booked', 'InProgress'", RequestStates.ActiveSqlList);
    }

    [Fact]
    public void Error_types_hold_every_contract_type_once_with_the_two_new_ones()
    {
        var expected = new[]
        {
            "TripExecutionDisabled", "PlanLocked", "TripNameInvalid", "NotTripOwner", "TaxiRequestNotFound", "TaxiRequestAlreadyActive",
            "TaxiRequestStateInvalid", "TaxiRequestLimitReached", "LegacyTaxiActive", "TaxiRequestActive", "TaxiSectionsRequired",
            "PickupNotAvailable", "DropNotAvailable", "NoDriversNearHere", "SightseeingDaysInvalid", "TripDatesRequired", "DayInPast",
            "AllDriversBusy", "ReasonRequired", "OfferNotFound", "NotOfferDriver", "OfferMissed", "OfferStateInvalid", "ConfirmWindowPassed",
            "DatesAlreadyBooked", "RatingNotAllowed", "AlreadyRated",
        };
        var consts = Consts(typeof(TaxiRequestErrorTypes)).ToArray();
        Assert.Equal(consts.Length, consts.Distinct().Count());
        Assert.Equal(consts.OrderBy(x => x), TaxiRequestErrorTypes.All.OrderBy(x => x));
        Assert.All(expected, e => Assert.Contains(e, consts));
        Assert.Contains("LegacyTaxiActive", consts);
        Assert.Contains("TaxiRequestActive", consts);
    }

    [Fact]
    public void Error_messages_match_the_contract_for_the_two_new_types()
    {
        Assert.Equal("This trip already has a taxi ask from an older app version. Cancel it before you send a new request.", TaxiRequestErrorTypes.Messages["LegacyTaxiActive"]);
        Assert.Equal("This trip already has a taxi request. Update the app to see it.", TaxiRequestErrorTypes.Messages["TaxiRequestActive"]);
        Assert.Equal("Dates already booked. You have another booking from 20 Dec to 24 Dec.", TaxiRequestErrorTypes.DatesAlreadyBookedMessage(new(2026, 12, 20), new(2026, 12, 24)));
    }

    [Fact]
    public void Dispatch_numbers_equal_contract_section_12()
    {
        Assert.Equal(300, TaxiDispatchRules.RingSeconds);
        Assert.Equal(900, TaxiDispatchRules.ConfirmSeconds);
        Assert.Equal(3, TaxiDispatchRules.AcceptBatch);
        Assert.Equal(9, TaxiDispatchRules.MaxAccepts);
        Assert.Equal(3, TaxiDispatchRules.MaxLiveRings);
        Assert.Equal(600, TaxiDispatchRules.QueueWaitSeconds);
        Assert.Equal(3, TaxiDispatchRules.RingMissLimit);
        Assert.Equal(6, TaxiDispatchRules.TestRingsPerHour);
        Assert.Equal(10, TaxiDispatchRules.CreatesPerHour);
        Assert.Equal(1, TaxiDispatchRules.TripNameMinLength);
        Assert.Equal(60, TaxiDispatchRules.TripNameMaxLength);
        Assert.Equal(1_000_000, TaxiDispatchRules.FairQueueStride);
    }

    [Fact]
    public void Stale_due_is_midnight_ist_after_the_first_needed_day()
    {
        // 00:00 IST on 19 Dec = 18 Dec 18:30 UTC
        Assert.Equal(new DateTime(2026, 12, 18, 18, 30, 0, DateTimeKind.Utc), TaxiDispatchRules.StaleDueAt(new DateOnly(2026, 12, 18)));
    }

    [Fact]
    public void Booking_start_and_auto_complete_times()
    {
        Assert.Equal(new DateTime(2026, 12, 17, 18, 30, 0, DateTimeKind.Utc), TaxiDispatchRules.BookingStartAt(new DateOnly(2026, 12, 18)));
        Assert.Equal(new DateTime(2026, 12, 21, 18, 35, 0, DateTimeKind.Utc), TaxiDispatchRules.AutoCompleteAt(new DateOnly(2026, 12, 21)));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(2, 5)]
    [InlineData(7, 9)]
    [InlineData(9, 9)]
    public void Ask_more_adds_three_and_stops_at_nine(int accepted, int target)
        => Assert.Equal(target, TaxiDispatchRules.AskMoreTarget(accepted));

    [Theory]
    [InlineData("Open", 0, 3, false)]
    [InlineData("Collecting", 2, 3, true)]
    [InlineData("Collecting", 3, 3, true)]
    [InlineData("Collecting", 3, 9, false)]
    [InlineData("Selected", 3, 3, false)]
    public void Can_ask_more_follows_the_contract_note(string state, int accepted, int target, bool expected)
        => Assert.Equal(expected, TaxiDispatchRules.CanAskMore(state, accepted, target));

    [Theory]
    [InlineData(3, 0, 0, 3)]
    [InlineData(3, 2, 0, 1)]
    [InlineData(3, 1, 1, 1)]
    [InlineData(9, 0, 0, 3)]
    [InlineData(3, 3, 0, 0)]
    [InlineData(3, 2, 2, 0)]
    public void Rings_needed_never_exceed_three_or_go_negative(int target, int accepted, int ringing, int expected)
        => Assert.Equal(expected, TaxiDispatchRules.RingsNeeded(target, accepted, ringing));

    [Fact]
    public void Still_asking_rule()
    {
        Assert.True(TaxiDispatchRules.StillAsking("Open", false, true, 0, 3));
        Assert.True(TaxiDispatchRules.StillAsking("Collecting", false, false, 1, 3));
        Assert.False(TaxiDispatchRules.StillAsking("Collecting", true, true, 1, 3));
        Assert.False(TaxiDispatchRules.StillAsking("Collecting", false, false, 3, 3));
        Assert.False(TaxiDispatchRules.StillAsking("Selected", false, true, 1, 3));
    }

    [Fact]
    public void Plan_lock_reason_follows_the_request_state()
    {
        Assert.Equal("TaxiRequestActive", PlanLockReasons.For("Open"));
        Assert.Equal("TaxiRequestActive", PlanLockReasons.For("Selected"));
        Assert.Equal("TaxiBookingActive", PlanLockReasons.For("Booked"));
        Assert.Equal("TaxiBookingActive", PlanLockReasons.For("InProgress"));
        Assert.Null(PlanLockReasons.For("Completed"));
        Assert.Null(PlanLockReasons.For("Expired"));
        Assert.Equal("Plan is locked while your taxi request is active. Cancel the request to edit.", PlanLockReasons.MessageFor("TaxiRequestActive"));
        Assert.Equal("Plan is locked while your booking is active. Cancel the booking to edit.", PlanLockReasons.MessageFor("TaxiBookingActive"));
    }

    [Theory]
    [InlineData("Ringing", false, "New")]
    [InlineData("Accepted", false, "New")]
    [InlineData("Selected", false, "New")]
    [InlineData("Confirmed", true, "Booked")]
    [InlineData("Confirmed", false, "Earlier")]
    [InlineData("Missed", false, "Earlier")]
    [InlineData("NotSelected", false, "Earlier")]
    [InlineData("NotAgreed", false, "Earlier")]
    [InlineData("Closed", false, "Earlier")]
    [InlineData("Withdrawn", false, "Earlier")]
    [InlineData("Declined", false, null)]
    public void Offer_bucket_follows_the_contract(string state, bool bookingActive, string? bucket)
        => Assert.Equal(bucket, OfferBuckets.For(state, bookingActive));

    [Theory]
    [InlineData("Filled", "Taken by another driver")]
    [InlineData("Booked", "Taken by another driver")]
    [InlineData("RequestCancelled", "The traveller cancelled the request")]
    [InlineData("DriverOffline", "You are offline")]
    [InlineData("Expired", null)]
    public void Ring_end_body_by_reason(string reason, string? body)
        => Assert.Equal(body, OfferEndReasons.RingEndBody(reason));

    [Fact]
    public void Routes_and_push_names_are_unique()
    {
        var routes = Consts(typeof(TaxiRequestRoutes)).Where(c => c.StartsWith('/')).ToArray();
        Assert.Equal(routes.Length, routes.Distinct().Count());
        Assert.Equal("taxi_request_3f9a6c2d-1b7e-4f0a-8c55-2e7d9b4a6101", TaxiRequestRoutes.HubGroup(Guid.Parse("3f9a6c2d-1b7e-4f0a-8c55-2e7d9b4a6101")));
        Assert.Equal("taxi_ring_a1b2c3d4-0001-4a00-8000-000000000001", TaxiPushKinds.RingTag(Guid.Parse("a1b2c3d4-0001-4a00-8000-000000000001")));
    }

    [Fact]
    public void Twelve_notice_kinds_map_to_twelve_distinct_types_that_have_wire_values()
    {
        var kinds = Enum.GetValues<TaxiNoticeKind>();
        Assert.Equal(12, kinds.Length);
        var types = kinds.Select(TaxiNoticeTypes.TypeOf).ToArray();
        Assert.Equal(12, types.Distinct().Count());
        Assert.All(types, t => Assert.NotEqual(t, NotificationTypes.ToWireValue(t)));
        Assert.Contains(nameof(TaxiNoticeKind.RingsMissed), kinds.Select(k => k.ToString()));
    }
}
