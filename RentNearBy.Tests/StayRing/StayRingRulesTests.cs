using System.Text.Json;
using RentNearBy.Core.Models.StayRing;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.StayRing;

// Stay enquiry ring (SPEC F:/Dhasu/Buddy_resume/stay_ring/SPEC.md): pure rules and payloads, per use case.
public class StayRingRulesTests
{
    private static readonly Guid Enquiry = Guid.Parse("5a5a5a5a-0001-4a00-8000-000000000001");
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly DateTime Now = new(2026, 12, 1, 22, 30, 0, DateTimeKind.Utc);

    private static StayRingSnapshot Snapshot(string? name = "Priya Sharma 9876543210", int? price = 4500) => new(
        Enquiry, name, "Pine View Stay", new DateOnly(2026, 12, 18), new DateOnly(2026, 12, 20), 2, 1, 1, price, TripOwnerBuckets.New);

    [Fact]
    public void Wire_values_of_the_three_new_types()
    {
        Assert.Equal("stay_enquiry_ring", NotificationTypes.ToWireValue(NotificationTypes.StayEnquiryRing));
        Assert.Equal("stay_enquiry_ring_ended", NotificationTypes.ToWireValue(NotificationTypes.StayEnquiryRingEnded));
        Assert.Equal("stay_enquiry_ring_missed", NotificationTypes.ToWireValue(NotificationTypes.StayEnquiryRingMissed));
    }

    // Use case 13: the generalisation keeps the taxi types where they were.
    [Fact]
    public void Ring_push_types_cover_both_kinds_and_keep_taxi()
    {
        Assert.True(RingPushTypes.IsRing(NotificationTypes.TaxiRequestRing));
        Assert.True(RingPushTypes.IsRing(NotificationTypes.StayEnquiryRing));
        Assert.False(RingPushTypes.IsRing(NotificationTypes.TripEnquiryReceived));
        Assert.True(RingPushTypes.IsRingEnd(NotificationTypes.TaxiRequestRingEnded));
        Assert.True(RingPushTypes.IsRingEnd(NotificationTypes.StayEnquiryRingEnded));
        Assert.True(RingPushTypes.IsRingEnd(NotificationTypes.StayEnquiryRingMissed));
        Assert.False(RingPushTypes.IsRingEnd(NotificationTypes.TaxiRequestRing));
        Assert.Equal(RingKinds.Taxi, RingPushTypes.KindOf(NotificationTypes.TaxiRequestRing));
        Assert.Equal(RingKinds.Stay, RingPushTypes.KindOf(NotificationTypes.StayEnquiryRingMissed));
        Assert.Null(RingPushTypes.KindOf(NotificationTypes.TripEnquiryReceived));
    }

    [Fact]
    public void Keys_and_tags_never_collide_with_taxi()
    {
        Assert.Equal("stayring:" + Enquiry.ToString("D"), StayRingPushKinds.RingKey(Enquiry));
        Assert.Equal("stayringmiss:" + Enquiry.ToString("D"), StayRingPushKinds.MissedKey(Enquiry));
        Assert.Equal("stayringend:" + Enquiry.ToString("D"), StayRingPushKinds.RingEndKey(Enquiry));
        Assert.StartsWith("stayringend:" + Enquiry.ToString("D") + ":", StayRingPushKinds.AnswerEndKey(Enquiry, Guid.NewGuid()));
        Assert.NotEqual(StayRingPushKinds.AnswerEndKey(Enquiry, Guid.NewGuid()), StayRingPushKinds.AnswerEndKey(Enquiry, Guid.NewGuid()));
        Assert.Equal("stay_ring_" + Enquiry.ToString("D"), StayRingPushKinds.RingTag(Enquiry));
        Assert.False(StayRingPushKinds.RingKey(Enquiry).StartsWith("ring:", StringComparison.Ordinal));
        Assert.False(StayRingPushKinds.RingEndKey(Enquiry).StartsWith("ringend:", StringComparison.Ordinal));
        Assert.False(StayRingPushKinds.RingTag(Enquiry).StartsWith(TaxiPushKinds.RingTag(Guid.Empty), StringComparison.Ordinal));
        Assert.True(StayRingPushKinds.TryParseMissedKey(StayRingPushKinds.MissedKey(Enquiry), out var parsed));
        Assert.Equal(Enquiry, parsed);
        Assert.False(StayRingPushKinds.TryParseMissedKey(StayRingPushKinds.RingKey(Enquiry), out _));
        Assert.False(StayRingPushKinds.TryParseMissedKey("stayringmiss:not-a-guid", out _));
        Assert.False(StayRingPushKinds.TryParseMissedKey(null, out _));
    }

    // Use case 2: Stay Owner role, live stay, switch on; anything else never rings.
    [Theory]
    [InlineData("stay", StayStatuses.Approved, false, true, true)]
    [InlineData("stay", StayStatuses.Approved, false, false, false)]
    [InlineData("stay", StayStatuses.Approved, true, true, false)]
    [InlineData("stay", StayStatuses.PendingReview, false, true, false)]
    [InlineData("stay", StayStatuses.Rejected, false, true, false)]
    [InlineData("seeker", StayStatuses.Approved, false, true, false)]
    [InlineData("taxi", StayStatuses.Approved, false, true, false)]
    public void Only_a_live_stay_owner_with_the_switch_on_is_rung(string role, string status, bool deleted, bool ringOn, bool expected)
    {
        var roleId = role switch { "stay" => UserRoles.StayOwnerId, "taxi" => UserRoles.TaxiDriverId, _ => UserRoles.SeekerId };
        Assert.Equal(expected, StayRingRules.CanRing(roleId, status, deleted, ringOn));
    }

    // Use case 3 + OWNER CLARIFICATION: no quiet hours, no daily cap: the rule has no clock at all; the window is the taxi window.
    [Fact]
    public void Ring_window_is_the_taxi_window_and_never_outlives_the_enquiry()
    {
        Assert.Equal(TaxiDispatchRules.RingSeconds, StayRingRules.RingSeconds);
        Assert.Equal(TaxiDispatchRules.TestRingSeconds, StayRingRules.TestRingSeconds);
        Assert.Equal(TaxiDispatchRules.TestRingsPerHour, StayRingRules.TestRingsPerHour);
        Assert.Equal(Now.AddSeconds(300), StayRingRules.RingExpiresAt(Now, Now.AddHours(24)));
        Assert.Equal(Now.AddSeconds(90), StayRingRules.RingExpiresAt(Now, Now.AddSeconds(90)));
        var night = new DateTime(2026, 12, 1, 19, 0, 0, DateTimeKind.Utc); // 00:30 IST
        Assert.Equal(night.AddSeconds(300), StayRingRules.RingExpiresAt(night, night.AddHours(24)));
    }

    // Use cases 7, 8: the enquiry status says when and why the ring ends.
    [Theory]
    [InlineData(TripEnquiryStatuses.Asked, null)]
    [InlineData(TripEnquiryStatuses.Offered, StayRingEndReasons.Answered)]
    [InlineData(TripEnquiryStatuses.Countered, StayRingEndReasons.Answered)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, StayRingEndReasons.Answered)]
    [InlineData(TripEnquiryStatuses.NotAvailable, StayRingEndReasons.Answered)]
    [InlineData(TripEnquiryStatuses.Declined, StayRingEndReasons.Answered)]
    [InlineData(TripEnquiryStatuses.CancelledByOwner, StayRingEndReasons.Answered)]
    [InlineData(TripEnquiryStatuses.Expired, StayRingEndReasons.Closed)]
    [InlineData(TripEnquiryStatuses.CancelledByTraveller, StayRingEndReasons.Closed)]
    [InlineData(TripEnquiryStatuses.Closed, StayRingEndReasons.Closed)]
    [InlineData(TripEnquiryStatuses.Failed, StayRingEndReasons.Closed)]
    public void End_reason_follows_the_enquiry_status(string status, string? reason)
        => Assert.Equal(reason, StayRingRules.EndReasonOf(status));

    [Fact]
    public void Every_enquiry_status_has_a_decided_end_reason()
        => Assert.All(TripEnquiryStatuses.All, s => Assert.Equal(s == TripEnquiryStatuses.Asked, StayRingRules.EndReasonOf(s) is null));

    // Use case 14: first name only, no phone, no user id of the traveller, no contact; every value a string.
    [Fact]
    public void Ring_payload_shows_the_enquiry_and_nothing_private()
    {
        var data = StayRingPayload.Build(Snapshot(), Now.AddSeconds(300), Now, Owner);

        Assert.Equal("stay_enquiry_ring", data["notification_type"]);
        Assert.Equal(StayRingPushKinds.Ring, data["kind"]);
        Assert.Equal(RingKinds.Stay, data["ringKind"]);
        Assert.Equal("1", data["v"]);
        Assert.Equal(Enquiry.ToString("D"), data["enquiryId"]);
        Assert.Equal("2026-12-01T22:35:00Z", data["expiresAt"]);
        Assert.Equal("2026-12-01T22:30:00Z", data["sentAt"]);
        Assert.Equal("Priya", data["guestName"]);
        Assert.Equal("Pine View Stay", data["stayName"]);
        Assert.Equal("2026-12-18", data["checkIn"]);
        Assert.Equal("2026-12-20", data["checkOut"]);
        Assert.Equal("2", data["nights"]);
        Assert.Equal("2", data["adults"]);
        Assert.Equal("1", data["kids"]);
        Assert.Equal("1", data["rooms"]);
        Assert.Equal("4500", data["price"]);
        Assert.Equal("2 nights · 18 to 20 Dec", data["line1"]);
        Assert.Equal("Pine View Stay", data["line2"]);
        Assert.Equal("2 adults, 1 kid · 1 room · Rs 4,500", data["line3"]);
        Assert.Equal(UserNotificationRoutes.StayEnquiries, data["action_route"]);
        Assert.Equal("{\"enquiryId\":\"" + Enquiry.ToString("D") + "\",\"bucket\":\"new\"}", data["action_args_json"]);
        Assert.Equal(Owner.ToString("D"), data[StayRingPayload.OwnerUserIdKey]);

        Assert.All(data.Keys, k => Assert.DoesNotContain("phone", k, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(data.Keys, k => k is "travellerUserId" or "otherPartyId" or "conversationId" or "whatsapp" or "ackToken");
        Assert.All(data.Values, v => Assert.DoesNotContain("9876543210", v));
    }

    [Fact]
    public void Price_is_left_out_when_not_given_and_a_missing_name_reads_guest()
    {
        var data = StayRingPayload.Build(Snapshot(name: null, price: null), Now.AddSeconds(300), Now, Owner);
        Assert.Equal(string.Empty, data["price"]);
        Assert.Equal("2 adults, 1 kid · 1 room", data["line3"]);
        Assert.Equal("Guest", data["guestName"]);
    }

    [Theory]
    [InlineData(2026, 12, 18, 2026, 12, 19, "1 night · 18 Dec")]
    [InlineData(2026, 12, 18, 2026, 12, 21, "3 nights · 18 to 21 Dec")]
    [InlineData(2026, 12, 30, 2027, 1, 2, "3 nights · 30 Dec to 2 Jan")]
    public void Dates_line(int y1, int m1, int d1, int y2, int m2, int d2, string expected)
        => Assert.Equal(expected, StayRingPayload.DatesLine(new DateOnly(y1, m1, d1), new DateOnly(y2, m2, d2)));

    [Fact]
    public void Guests_line_leaves_out_what_is_not_given()
    {
        Assert.Equal("1 adult", StayRingPayload.GuestsLine(1, 0, null, null));
        Assert.Equal("3 adults · 2 rooms", StayRingPayload.GuestsLine(3, null, 2, 0));
        Assert.Equal(string.Empty, StayRingPayload.GuestsLine(null, null, null, null));
    }

    // Use case 9: the missed ring points back to the same enquiry, never changes it; no phone either.
    [Fact]
    public void Missed_ring_points_to_the_enquiry()
    {
        var missed = StayRingPayload.BuildMissed(Snapshot(), Owner);
        Assert.Equal("stay_enquiry_ring_missed", missed.Data["notification_type"]);
        Assert.Equal(StayRingPushKinds.Missed, missed.Data["kind"]);
        Assert.Equal(UserNotificationRoutes.StayEnquiries, missed.Data["action_route"]);
        Assert.Contains(Enquiry.ToString("D"), missed.Data["action_args_json"]);
        Assert.Equal(StayRingPayload.MissedTitle, missed.Title);
        Assert.Equal("Priya · 2 nights · 18 to 20 Dec · Pine View Stay. Reply in Enquiries.", missed.Body);
        Assert.DoesNotContain("9876543210", missed.Body);
    }

    [Theory]
    [InlineData(StayRingEndReasons.Answered)]
    [InlineData(StayRingEndReasons.Closed)]
    [InlineData(StayRingEndReasons.Accepted)]
    [InlineData(StayRingEndReasons.Declined)]
    public void Ring_end_carries_its_reason(string reason)
    {
        var end = StayRingPayload.BuildEnd(Enquiry, reason, Owner);
        Assert.Equal("stay_enquiry_ring_ended", end.Data["notification_type"]);
        Assert.Equal(StayRingPushKinds.RingEnd, end.Data["kind"]);
        Assert.Equal(reason, end.Data["reason"]);
        Assert.Equal(Enquiry.ToString("D"), end.Data["enquiryId"]);
        Assert.Equal(StayRingEndReasons.Body(reason), end.Body);
        Assert.False(string.IsNullOrWhiteSpace(end.Body));
    }

    // Use case 6: Decline only ignores; its copy says the enquiry stays.
    [Fact]
    public void Decline_copy_says_the_enquiry_stays()
    {
        Assert.Contains("still in Enquiries", StayRingEndReasons.Body(StayRingEndReasons.Declined));
        Assert.True(StayRingEndReasons.IsOutcome("Accepted"));
        Assert.True(StayRingEndReasons.IsOutcome("Declined"));
        Assert.False(StayRingEndReasons.IsOutcome("Answered"));
        Assert.False(StayRingEndReasons.IsOutcome("accepted"));
        Assert.False(StayRingEndReasons.IsOutcome(null));
    }

    [Fact]
    public void Test_ring_has_the_ring_shape_with_an_empty_enquiry()
    {
        var data = StayRingPayload.BuildTest(Now, Now.AddSeconds(30), Owner);
        Assert.Equal("stay_enquiry_ring", data["notification_type"]);
        Assert.Equal(StayRingPushKinds.RingTest, data["kind"]);
        Assert.Equal(string.Empty, data["enquiryId"]);
        Assert.Equal(UserNotificationRoutes.StayRingReady, data["action_route"]);
        var ring = StayRingPayload.Build(Snapshot(), Now.AddSeconds(300), Now, Owner);
        Assert.Equal(ring.Keys.Order(), data.Keys.Order());
    }

    [Fact]
    public void Every_payload_value_serialises_as_a_string()
    {
        foreach (var map in new IReadOnlyDictionary<string, string>[]
                 {
                     StayRingPayload.Build(Snapshot(), Now.AddSeconds(300), Now, Owner), StayRingPayload.BuildEnd(Enquiry, "Answered", Owner).Data,
                     StayRingPayload.BuildMissed(Snapshot(), Owner).Data, StayRingPayload.BuildTest(Now, Now.AddSeconds(30), Owner),
                 })
        {
            var json = JsonDocument.Parse(JsonSerializer.Serialize(map)).RootElement;
            Assert.All(json.EnumerateObject(), p => Assert.Equal(JsonValueKind.String, p.Value.ValueKind));
        }
    }
}
