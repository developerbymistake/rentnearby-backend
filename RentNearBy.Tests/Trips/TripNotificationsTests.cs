using System.Reflection;
using System.Text.Json;

namespace RentNearBy.Tests.Trips;

public class TripNotificationsTests
{
    private const string UrgentPrefix = "Urgent · ";

    private static readonly (string Type, string Wire)[] Rows =
    [
        (NotificationTypes.TripEnquiryReceived, "trip_enquiry_received"),
        (NotificationTypes.TripOfferReceived, "trip_offer_received"),
        (NotificationTypes.TripOfferAccepted, "trip_offer_accepted"),
        (NotificationTypes.TripOfferDeclined, "trip_offer_declined"),
        (NotificationTypes.TripPhoneBookingRecorded, "trip_phone_booking_recorded"),
        (NotificationTypes.TripHostConfirmed, "trip_host_confirmed"),
        (NotificationTypes.TripHostDenied, "trip_host_denied"),
        (NotificationTypes.TripHostLinkAnswered, "trip_host_link_answered"),
        (NotificationTypes.TripListingRemoved, "trip_listing_removed"),
        (NotificationTypes.TripTravellerLeft, "trip_traveller_left"),
        (NotificationTypes.TripEnquiryExpired, "trip_enquiry_expired"),
        (NotificationTypes.TripNotAvailable, "trip_not_available"),
        (NotificationTypes.TripTravellerCancelled, "trip_traveller_cancelled"),
        (NotificationTypes.TripOwnerCancelled, "trip_owner_cancelled"),
        (NotificationTypes.TripLeadReceived, "trip_lead_received"),
        (NotificationTypes.TripAdvanceRequested, "trip_advance_requested"),
        (NotificationTypes.TripAdvanceProofSent, "trip_advance_proof_sent"),
        (NotificationTypes.TripAdvanceNotYet, "trip_advance_not_yet"),
        (NotificationTypes.TripAdvanceReceived, "trip_advance_received"),
        (NotificationTypes.TripHoldExpired, "trip_hold_expired"),
        (NotificationTypes.TripDriverFare, "trip_driver_fare"),
        (NotificationTypes.TripTaxiBookedByTraveller, "trip_taxi_booked_by_traveller"),
        (NotificationTypes.TripBookingChanged, "trip_booking_changed"),
        (NotificationTypes.TripDriverConfirmed, "trip_driver_confirmed"),
        (NotificationTypes.TripDriverDenied, "trip_driver_denied"),
        (NotificationTypes.TripEnquiryClosedByAdmin, "trip_enquiry_closed_by_admin"),
        (NotificationTypes.TripCounterReceived, "trip_counter_received"),
        (NotificationTypes.TripRevisedOffer, "trip_revised_offer"),
        (NotificationTypes.TripRevisedKept, "trip_revised_kept"),
        (NotificationTypes.TripChangeRequest, "trip_change_request"),
        (NotificationTypes.TripChangeAccepted, "trip_change_accepted"),
        (NotificationTypes.TripHostNotConfirmed, "trip_host_not_confirmed"),
        (NotificationTypes.TripNoReply, "trip_no_reply"),
        (NotificationTypes.TripProblemReported, "trip_problem_reported"),
        (NotificationTypes.TripStartsIn3Days, "trip_starts_in_3_days"),
        (NotificationTypes.TripStartsToday, "trip_starts_today"),
        (NotificationTypes.TripMarkComplete, "trip_mark_complete"),
    ];

    private static readonly HashSet<string> AllowedRoutes =
    [
        UserNotificationRoutes.TripPlan,
        UserNotificationRoutes.StayEnquiries,
        UserNotificationRoutes.ChatConversation,
        UserNotificationRoutes.TripLeads,
        UserNotificationRoutes.MyTrips,
    ];

    [Fact]
    public void Types_have_the_contract_wire_values()
    {
        foreach (var (type, wire) in Rows)
            Assert.Equal(wire, NotificationTypes.ToWireValue(type));
    }

    [Fact]
    public void Types_are_unique()
    {
        Assert.Equal(Rows.Length, Rows.Select(r => r.Type).Distinct().Count());
        Assert.Equal(Rows.Length, Rows.Select(r => r.Wire).Distinct().Count());
    }

    [Fact]
    public void Routes_match_the_app_routes()
    {
        Assert.Equal("/trip-plan", UserNotificationRoutes.TripPlan);
        Assert.Equal("/stay-enquiries", UserNotificationRoutes.StayEnquiries);
        Assert.Equal("/chat-conversation", UserNotificationRoutes.ChatConversation);
        Assert.Equal("/trip-leads", UserNotificationRoutes.TripLeads);
        Assert.Equal("/my-trips", UserNotificationRoutes.MyTrips);
    }

    [Fact]
    public void Every_row_has_a_builder()
    {
        var builders = Builders();
        var reminders = ReminderEvents().Length;
        Assert.True(builders.Count + reminders >= Rows.Length, $"found {builders.Count} builders and {reminders} reminder builders for {Rows.Length} rows");
        var types = builders.Select(b => Build(b, urgent: false).Type).ToHashSet();
        types.UnionWith(ReminderEvents().Select(n => n.Type));
        foreach (var (type, _) in Rows)
            Assert.Contains(type, types);
    }

    [Fact]
    public void Every_builder_fills_type_text_route_and_args()
    {
        foreach (var b in Builders())
        {
            var n = Build(b, urgent: false);
            var name = b.Name;
            Assert.True(Rows.Any(r => r.Type == n.Type), $"{name}: unknown type {n.Type}");
            Assert.NotEqual(n.Type, NotificationTypes.ToWireValue(n.Type));
            Assert.False(string.IsNullOrWhiteSpace(n.Title), $"{name}: title");
            Assert.False(string.IsNullOrWhiteSpace(n.Body), $"{name}: body");
            Assert.Contains(n.ActionRoute, AllowedRoutes);
            Assert.False(string.IsNullOrEmpty(n.ActionArgumentsJson), $"{name}: args");
            using var doc = JsonDocument.Parse(n.ActionArgumentsJson!);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
    }

    [Fact]
    public void Trip_plan_routes_carry_the_trip_id()
    {
        foreach (var b in Builders())
        {
            var n = Build(b, urgent: false);
            if (n.ActionRoute != UserNotificationRoutes.TripPlan) continue;
            using var doc = JsonDocument.Parse(n.ActionArgumentsJson!);
            Assert.True(doc.RootElement.TryGetProperty("tripId", out _), $"{b.Name}: tripId");
        }
    }

    [Fact]
    public void Enquiry_routes_carry_the_enquiry_id()
    {
        foreach (var b in Builders())
        {
            var n = Build(b, urgent: false);
            if (n.ActionRoute != UserNotificationRoutes.StayEnquiries) continue;
            using var doc = JsonDocument.Parse(n.ActionArgumentsJson!);
            Assert.True(doc.RootElement.TryGetProperty("enquiryId", out _), $"{b.Name}: enquiryId");
        }
    }

    [Fact]
    public void Chat_routes_carry_the_full_argument_map()
    {
        string[] keys = ["conversationId", "listingType", "listingId", "otherPartyId", "otherPartyName", "isOwner", "status", "isBlockedByMe"];
        foreach (var b in Builders())
        {
            var n = Build(b, urgent: false);
            if (n.ActionRoute != UserNotificationRoutes.ChatConversation) continue;
            using var doc = JsonDocument.Parse(n.ActionArgumentsJson!);
            foreach (var k in keys)
                Assert.True(doc.RootElement.TryGetProperty(k, out _), $"{b.Name}: {k}");
            Assert.Equal("Stay", doc.RootElement.GetProperty("listingType").GetString());
        }
    }

    [Fact]
    public void Urgent_prefix_only_when_urgent()
    {
        var knobs = 0;
        foreach (var b in Builders())
        {
            if (!HasUrgentKnob(b)) continue;
            knobs++;
            var plain = Build(b, urgent: false);
            var urgent = Build(b, urgent: true);
            Assert.False(plain.Title.StartsWith(UrgentPrefix), $"{b.Name}: plain title has the prefix");
            Assert.StartsWith(UrgentPrefix, urgent.Title);
            Assert.Equal(plain.Type, urgent.Type);
        }
        Assert.True(knobs > 0, "no builder takes an urgent flag");
    }

    [Fact]
    public void No_text_claims_verification()
    {
        foreach (var b in Builders())
        {
            var n = Build(b, urgent: true);
            var all = (n.Title + " " + n.Body).ToLowerInvariant();
            Assert.DoesNotContain("verified", all);
            Assert.DoesNotContain("guaranteed", all);
        }
    }

    private static readonly Guid Traveller = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Owner = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid TripId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static TripEnquiry TaxiRow(bool urgent = false) => new()
    {
        Id = Guid.NewGuid(),
        TripId = TripId,
        Kind = TripEnquiryKinds.Taxi,
        TravellerUserId = Traveller,
        OwnerUserId = Owner,
        ListingName = "Bisht Cabs",
        CheckIn = new DateOnly(2026, 11, 1),
        CheckOut = new DateOnly(2026, 11, 3),
        DaysJson = "[1,2]",
        ContextJson = """{"city":"Nainital","adults":2,"kids":1}""",
        PickupTime = "08:30",
        ListedRateSnapshot = 1800,
        DriverFare = 3800,
        AgreedFare = 3800,
        IsUrgent = urgent,
    };

    private static TripEnquiry StayRow() => new()
    {
        Id = Guid.NewGuid(),
        TripId = TripId,
        Kind = TripEnquiryKinds.Stay,
        TravellerUserId = Traveller,
        OwnerUserId = Owner,
        ListingName = "Deodar Homestay",
        CheckIn = new DateOnly(2026, 11, 1),
        CheckOut = new DateOnly(2026, 11, 2),
        ContextJson = """{"city":"Nainital","adults":2,"kids":0}""",
        AdvanceAmount = 500,
        HoldUntil = new DateTime(2026, 11, 1, 12, 30, 0, DateTimeKind.Utc),
        IsUrgent = false,
    };

    private static TripPushContext Ctx(Guid recipient, string? chat = null, string? ownerName = "Mohan Bisht") =>
        new(recipient, "Rahul", "Bisht Cabs", chat, null, null, ownerName);

    [Fact]
    public void Taxi_not_available_says_driver_and_routes_to_the_plan()
    {
        var n = TripNotifications.TripNotAvailable(TaxiRow(), Ctx(Traveller));
        Assert.Contains("driver", n.Body);
        Assert.DoesNotContain("stay", n.Body);
        Assert.Equal(UserNotificationRoutes.TripPlan, n.ActionRoute);
    }

    [Fact]
    public void Stay_not_available_still_says_stay()
    {
        var n = TripNotifications.TripNotAvailable(StayRow(), Ctx(Traveller));
        Assert.Contains("stay", n.Body);
        Assert.DoesNotContain("driver", n.Body);
    }

    [Fact]
    public void Taxi_expired_says_driver()
    {
        var n = TripNotifications.TripEnquiryExpired(TaxiRow(), Ctx(Traveller));
        Assert.Contains("driver", n.Body);
    }

    [Theory]
    [InlineData("taxi")]
    [InlineData("stay")]
    public void Kind_aware_pick_another_bodies(string kind)
    {
        var e = kind == "taxi" ? TaxiRow() : StayRow();
        foreach (var n in new[]
        {
            TripNotifications.TripListingRemoved(e, Ctx(Traveller)),
            TripNotifications.TripOwnerCancelled(e, Ctx(Traveller)),
            TripNotifications.TripHostDenied(e, Ctx(Traveller)),
        })
            Assert.Contains($"Pick another {kind}", n.Body);
    }

    [Fact]
    public void Taxi_owner_pushes_route_to_trip_leads()
    {
        var e = TaxiRow();
        foreach (var n in new[]
        {
            TripNotifications.TripLeadReceived(e, Ctx(Owner)),
            TripNotifications.TripTaxiBookedByTraveller(e, Ctx(Owner)),
            TripNotifications.TripBookingChanged(e, Ctx(Owner)),
            TripNotifications.TripTravellerCancelled(e, Ctx(Owner)),
            TripNotifications.TripTravellerLeft(e, Ctx(Owner)),
            TripNotifications.TripEnquiryClosedByAdmin(e, Ctx(Owner)),
        })
        {
            Assert.Equal(UserNotificationRoutes.TripLeads, n.ActionRoute);
            using var doc = JsonDocument.Parse(n.ActionArgumentsJson!);
            Assert.Equal(e.Id.ToString(), doc.RootElement.GetProperty("enquiryId").GetString());
        }
    }

    [Fact]
    public void Stay_owner_pushes_without_a_chat_route_to_enquiries()
    {
        var n = TripNotifications.TripTravellerCancelled(StayRow(), Ctx(Owner));
        Assert.Equal(UserNotificationRoutes.StayEnquiries, n.ActionRoute);
    }

    [Fact]
    public void Taxi_traveller_pushes_route_to_the_plan_with_the_trip_id()
    {
        var e = TaxiRow();
        foreach (var n in new[]
        {
            TripNotifications.TripDriverFare(e, Ctx(Traveller)),
            TripNotifications.TripDriverConfirmed(e, Ctx(Traveller)),
            TripNotifications.TripDriverDenied(e, Ctx(Traveller)),
            TripNotifications.TripEnquiryClosedByAdmin(e, Ctx(Traveller)),
        })
        {
            Assert.Equal(UserNotificationRoutes.TripPlan, n.ActionRoute);
            using var doc = JsonDocument.Parse(n.ActionArgumentsJson!);
            Assert.Equal(TripId.ToString(), doc.RootElement.GetProperty("tripId").GetString());
        }
    }

    [Fact]
    public void Driver_fare_names_the_driver_and_the_listed_total()
    {
        var n = TripNotifications.TripDriverFare(TaxiRow(), Ctx(Traveller));
        Assert.Equal("Mohan Bisht replied Rs 3,800", n.Title);
        Assert.Contains("Listed Rs 3,600", n.Body);
        Assert.Contains("I booked", n.Body);
    }

    [Fact]
    public void Driver_name_falls_back_to_the_listing_name()
    {
        var n = TripNotifications.TripDriverFare(TaxiRow(), Ctx(Traveller, ownerName: null));
        Assert.StartsWith("Bisht Cabs replied", n.Title);
    }

    [Fact]
    public void Lead_received_body_has_traveller_dates_people_and_pickup()
    {
        var n = TripNotifications.TripLeadReceived(TaxiRow(), Ctx(Owner));
        Assert.Equal("New trip lead", n.Title);
        Assert.Contains("Rahul", n.Body);
        Assert.Contains("01-02 Nov", n.Body);
        Assert.DoesNotContain("03 Nov", n.Body);
        Assert.Contains("2 adults, 1 kid", n.Body);
        Assert.Contains("pickup 8:30 am", n.Body);
        Assert.EndsWith(".", n.Body);
    }

    [Fact]
    public void Urgent_taxi_lead_gets_the_prefix()
    {
        var n = TripNotifications.TripLeadReceived(TaxiRow(urgent: true), Ctx(Owner));
        Assert.StartsWith(UrgentPrefix, n.Title);
    }

    [Fact]
    public void Booked_and_changed_pushes_carry_fare_and_trip_leads_wording()
    {
        var e = TaxiRow();
        var booked = TripNotifications.TripTaxiBookedByTraveller(e, Ctx(Owner));
        Assert.Equal("Rahul booked you", booked.Title);
        Assert.Contains("Rs 3,800", booked.Body);
        Assert.Contains("Confirm in Trip leads", booked.Body);
        var changed = TripNotifications.TripBookingChanged(e, Ctx(Owner));
        Assert.Equal("Booking changed by Rahul", changed.Title);
        Assert.Contains("Confirm again in Trip leads", changed.Body);
    }

    [Fact]
    public void Hold_expired_has_host_and_guest_wording()
    {
        var e = StayRow();
        var chat = SampleJson("chatargsjson");
        var guest = TripNotifications.TripHoldExpired(e, Ctx(Traveller, chat, null));
        var host = TripNotifications.TripHoldExpired(e, Ctx(Owner, chat, null));
        Assert.Equal("Hold expired", guest.Title);
        Assert.Contains("did not confirm the advance in time", guest.Body);
        Assert.Contains("You did not mark the advance of Rahul in time", host.Body);
        Assert.Equal(guest.Type, host.Type);
        Assert.Equal(UserNotificationRoutes.ChatConversation, guest.ActionRoute);
        Assert.Equal(UserNotificationRoutes.ChatConversation, host.ActionRoute);
    }

    [Fact]
    public void Hold_expired_uses_two_dedupe_keys_for_the_two_recipients()
    {
        var id = Guid.NewGuid();
        Assert.NotEqual(TripAdvanceRules.HoldKey(id, "t"), TripAdvanceRules.HoldKey(id, "o"));
    }

    [Fact]
    public void Advance_pushes_carry_amount_and_ist_time()
    {
        var e = StayRow();
        var chat = SampleJson("chatargsjson");
        var asked = TripNotifications.TripAdvanceRequested(e, Ctx(Traveller, chat, null));
        Assert.Contains("Rs 500", asked.Body);
        Assert.Contains("6:00 pm", asked.Body);
        Assert.Contains("Pay the host directly", asked.Body);
        var proof = TripNotifications.TripAdvanceProofSent(e, Ctx(Owner, chat, null));
        Assert.Equal("Rahul sent proof for Rs 500", proof.Title);
        Assert.Contains("mark it received", proof.Body);
        Assert.Contains("Hold ends 6:00 pm", proof.Body);
        var notYet = TripNotifications.TripAdvanceNotYet(e, Ctx(Traveller, chat, null));
        Assert.Contains("before 6:00 pm", notYet.Body);
        var received = TripNotifications.TripAdvanceReceived(e, Ctx(Traveller, chat, null));
        Assert.Equal("Advance received", received.Title);
        Assert.Contains("says your Rs 500 advance is received", received.Body);
    }

    [Fact]
    public void Advance_pushes_open_the_chat()
    {
        var chat = SampleJson("chatargsjson");
        var e = StayRow();
        Assert.Equal(UserNotificationRoutes.ChatConversation, TripNotifications.TripAdvanceRequested(e, Ctx(Traveller, chat, null)).ActionRoute);
        Assert.Equal(UserNotificationRoutes.ChatConversation, TripNotifications.TripAdvanceProofSent(e, Ctx(Owner, chat, null)).ActionRoute);
    }

    [Fact]
    public void Admin_close_includes_the_note()
    {
        var e = StayRow();
        e.CancelReason = "Fraud report confirmed";
        var n = TripNotifications.TripEnquiryClosedByAdmin(e, Ctx(Traveller));
        Assert.Equal("Enquiry closed by Bakhli", n.Title);
        Assert.Contains("Fraud report confirmed", n.Body);
    }

    [Fact]
    public void Taxi_texts_never_mention_payment_flows()
    {
        var e = TaxiRow();
        var all = new[]
        {
            TripNotifications.TripLeadReceived(e, Ctx(Owner)),
            TripNotifications.TripDriverFare(e, Ctx(Traveller)),
            TripNotifications.TripDriverConfirmed(e, Ctx(Traveller)),
            TripNotifications.TripDriverDenied(e, Ctx(Traveller)),
        };
        foreach (var n in all)
        {
            var t = (n.Title + " " + n.Body).ToLowerInvariant();
            Assert.DoesNotContain("advance", t);
            Assert.DoesNotContain("upi", t);
            Assert.DoesNotContain("verified", t);
        }
    }

    [Fact]
    public void No_push_text_carries_a_upi_id()
    {
        foreach (var b in Builders())
        {
            var n = Build(b, urgent: false);
            Assert.DoesNotContain("@", n.Title + " " + n.Body);
        }
    }

    private static NotificationEvent[] ReminderEvents() =>
    [
        TripReminderNotifications.StartsIn3Days(Traveller, TripId, "Nainital", "Stay confirmed. Taxi is booked by you."),
        TripReminderNotifications.StartsToday(Traveller, TripId, "Pickup 8:30 am. Tap for the driver's number."),
        TripReminderNotifications.MarkComplete(Traveller, TripId, "Nainital"),
    ];

    [Fact]
    public void Reminder_builders_open_the_plan_with_the_trip_id()
    {
        foreach (var n in ReminderEvents())
        {
            Assert.Equal(Traveller, n.TargetUserId);
            Assert.Equal(UserNotificationRoutes.TripPlan, n.ActionRoute);
            using var doc = JsonDocument.Parse(n.ActionArgumentsJson!);
            Assert.Equal(TripId.ToString(), doc.RootElement.GetProperty("tripId").GetString());
            Assert.DoesNotContain(UrgentPrefix, n.Title);
            Assert.Contains(n.Type, Rows.Select(x => x.Type));
        }
    }

    [Fact]
    public void Reminder_titles_and_bodies()
    {
        var e = ReminderEvents();
        Assert.Equal(NotificationTypes.TripStartsIn3Days, e[0].Type);
        Assert.Equal("Nainital in 3 days", e[0].Title);
        Assert.Equal("Stay confirmed. Taxi is booked by you.", e[0].Body);
        Assert.Equal(NotificationTypes.TripStartsToday, e[1].Type);
        Assert.Equal("Your trip starts today", e[1].Title);
        Assert.Equal(NotificationTypes.TripMarkComplete, e[2].Type);
        Assert.Equal("How was Nainital?", e[2].Title);
        Assert.Equal("Mark the trip complete.", e[2].Body);
    }

    [Theory]
    [InlineData(3, "Nainital in 3 days")]
    [InlineData(2, "Nainital in 2 days")]
    [InlineData(1, "Nainital tomorrow")]
    public void Starts_in_title_uses_the_real_days_left(int daysLeft, string title)
    {
        var n = TripReminderNotifications.StartsIn(Traveller, TripId, "Nainital", daysLeft, "x");
        Assert.Equal(title, n.Title);
        Assert.Equal(NotificationTypes.TripStartsIn3Days, n.Type);
    }

    [Fact]
    public void Reminder_city_falls_back_when_blank()
    {
        Assert.StartsWith("Your trip", TripReminderNotifications.StartsIn3Days(Traveller, TripId, " ", "x").Title);
    }

    [Fact]
    public void Negotiation_pushes_go_to_the_right_side()
    {
        var e = StayRow();
        var chat = SampleJson("chatargsjson");
        var ctx = new TripPushContext(Owner, "Rahul", "Deodar Homestay", chat, 3600, null, null);
        var tctx = new TripPushContext(Traveller, "Rahul", "Deodar Homestay", chat, 4200, null, null);
        var counter = TripNotifications.TripCounterReceived(e, ctx);
        Assert.Equal(Owner, counter.TargetUserId);
        Assert.Equal("Rahul proposed a price", counter.Title);
        Assert.Contains("Rs 3,600 a night", counter.Body);
        var revised = TripNotifications.TripRevisedOffer(e, tctx);
        Assert.Equal(Traveller, revised.TargetUserId);
        Assert.Equal("Deodar Homestay revised the price", revised.Title);
        Assert.Contains("Your price stays until you accept", revised.Body);
        Assert.Equal(Owner, TripNotifications.TripRevisedKept(e, ctx).TargetUserId);
        Assert.Equal(Owner, TripNotifications.TripChangeRequest(e, ctx).TargetUserId);
        Assert.Equal(Traveller, TripNotifications.TripChangeAccepted(e, tctx).TargetUserId);
        Assert.Equal(Traveller, TripNotifications.TripNoReply(e, tctx).TargetUserId);
        Assert.Equal(Traveller, TripNotifications.TripHostNotConfirmed(e, tctx).TargetUserId);
    }

    [Fact]
    public void Host_not_confirmed_prefers_the_phone_booking_host_name()
    {
        var e = StayRow();
        e.PhoneBookingJson = """{"hostName":"Ramesh","hostPhone":"9876543210"}""";
        var n = TripNotifications.TripHostNotConfirmed(e, new TripPushContext(Traveller, "Rahul", "Deodar Homestay", null, null, null, null));
        Assert.Equal("Ramesh has not confirmed yet", n.Title);
        Assert.Equal(UserNotificationRoutes.TripPlan, n.ActionRoute);
    }

    [Fact]
    public void Problem_reported_body_depends_on_kind()
    {
        var taxi = TripNotifications.TripProblemReported(TaxiRow(), Ctx(Owner));
        Assert.Contains("driver did not come", taxi.Body);
        Assert.Equal(UserNotificationRoutes.TripLeads, taxi.ActionRoute);
        var stay = TripNotifications.TripProblemReported(StayRow(), Ctx(Owner, SampleJson("chatargsjson")));
        Assert.Contains("host could not give the room", stay.Body);
    }

    private static List<MethodInfo> Builders() =>
        typeof(TripNotifications)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(NotificationEvent))
            .OrderBy(m => m.Name)
            .ToList();

    private static bool HasUrgentKnob(MethodInfo m) =>
        m.GetParameters().Any(p => p.Name != null && p.Name.Contains("urgent", StringComparison.OrdinalIgnoreCase))
        || m.GetParameters().Any(p => p.ParameterType.GetProperty("IsUrgent")?.CanWrite == true);

    private static NotificationEvent Build(MethodInfo m, bool urgent)
    {
        var args = m.GetParameters().Select(p => Make(p.ParameterType, p.Name, urgent, 0)).ToArray();
        try
        {
            return (NotificationEvent)m.Invoke(null, args)!;
        }
        catch (TargetInvocationException e)
        {
            throw new Xunit.Sdk.XunitException($"{m.Name} threw {e.InnerException}");
        }
    }

    // *Json members get real JSON: the chat argument map for ChatArgsJson, a context for ContextJson, {} otherwise.
    private static string SampleJson(string name) => name.ToLowerInvariant() switch
    {
        "chatargsjson" => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["conversationId"] = Guid.NewGuid(), ["listingType"] = "Stay", ["listingId"] = Guid.NewGuid(), ["stayTypeId"] = null,
            ["otherPartyId"] = Guid.NewGuid(), ["otherPartyName"] = "Rahul", ["listingTitle"] = "Deodar Homestay", ["area"] = "Nainital",
            ["isOwner"] = true, ["status"] = "Active", ["isBlockedByMe"] = false,
        }),
        "contextjson" => """{"city":"Nainital","adults":2,"kids":1,"travellerName":"Rahul"}""",
        _ => "{}",
    };

    private static object? Make(Type type, string? name, bool urgent, int depth)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(bool)) return name != null && name.Contains("urgent", StringComparison.OrdinalIgnoreCase) ? urgent : false;
        if (t == typeof(string)) return name != null && name.EndsWith("Json", StringComparison.OrdinalIgnoreCase) ? SampleJson(name) : "Sample";
        if (t == typeof(Guid)) return Guid.NewGuid();
        if (t == typeof(int)) return 3000;
        if (t == typeof(long)) return 3000L;
        if (t == typeof(short)) return (short)1;
        if (t == typeof(double)) return 1.5;
        if (t == typeof(DateOnly)) return new DateOnly(2026, 11, 1);
        if (t == typeof(DateTime)) return new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);
        if (t == typeof(TimeSpan)) return TimeSpan.FromHours(24);
        if (t.IsEnum) return Enum.GetValues(t).GetValue(0);
        if (t.IsArray) return Array.CreateInstance(t.GetElementType()!, 0);
        if (depth > 4) return null;

        object? instance;
        var ctor = t.GetConstructor(Type.EmptyTypes);
        if (ctor != null)
            instance = ctor.Invoke(null);
        else
        {
            var c = t.GetConstructors().OrderBy(x => x.GetParameters().Length).FirstOrDefault();
            if (c == null) return null;
            instance = c.Invoke(c.GetParameters().Select(p => Make(p.ParameterType, p.Name, urgent, depth + 1)).ToArray());
        }

        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
            var pt = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (pt == typeof(bool))
                prop.SetValue(instance, prop.Name.Contains("urgent", StringComparison.OrdinalIgnoreCase) && urgent);
            else if (pt == typeof(string) || pt == typeof(Guid) || pt == typeof(int) || pt == typeof(DateOnly) || pt == typeof(DateTime) || pt == typeof(short) || pt == typeof(double) || pt == typeof(long))
            {
                if (prop.Name == "Status" && t == typeof(TripEnquiry))
                    prop.SetValue(instance, TripEnquiryStatuses.Asked);
                else
                    prop.SetValue(instance, Make(prop.PropertyType, prop.Name, urgent, depth + 1));
            }
        }
        return instance;
    }
}
