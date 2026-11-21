using System.Text.Json;
using RentNearBy.Api.Handlers.Owner.Be4;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be4;

// AU-20: status priority (reply over confirmed), key changes with the facts, trip card state by dates, the chosen trip.
public class SeekerStatusRulesTests
{
    private static SeekerEnquiryFact F(string status, int minutes, string name = "Lake View")
        => new(Guid.NewGuid(), status, name, Be4Facts.Now.AddMinutes(minutes));

    [Fact]
    public void Reply_wins_over_confirmed()
    {
        var s = SeekerStatusRules.Of([F(TripEnquiryStatuses.ConfirmedByBoth, 10), F(TripEnquiryStatuses.Offered, 1, "Pine Stay"), F(TripEnquiryStatuses.Offered, 5, "Oak Stay")])!;
        Assert.Equal(SeekerStatusKinds.Reply, s.Kind);
        Assert.Equal(2, s.Count);
        Assert.Equal("Oak Stay", s.ListingName);
    }

    [Fact]
    public void Confirmed_when_no_reply_waits_and_null_when_nothing()
    {
        Assert.Equal(SeekerStatusKinds.Confirmed, SeekerStatusRules.Of([F(TripEnquiryStatuses.ConfirmedByBoth, 1), F(TripEnquiryStatuses.Asked, 2)])!.Kind);
        Assert.Null(SeekerStatusRules.Of([F(TripEnquiryStatuses.Asked, 1), F(TripEnquiryStatuses.Declined, 2)]));
        Assert.Null(SeekerStatusRules.Of([]));
    }

    [Fact]
    public void Key_changes_when_the_facts_change()
    {
        var a = F(TripEnquiryStatuses.Offered, 1);
        var k1 = SeekerStatusRules.Of([a])!.Key;
        Assert.Equal(k1, SeekerStatusRules.Of([a])!.Key);
        Assert.NotEqual(k1, SeekerStatusRules.Of([a, F(TripEnquiryStatuses.Offered, 0)])!.Key);
        Assert.NotEqual(k1, SeekerStatusRules.Of([a with { UpdatedAt = a.UpdatedAt.AddMinutes(1) }])!.Key);
    }

    [Theory]
    [InlineData("2026-11-21", "2026-11-23", SeekerTripStates.OnTrip)]
    [InlineData("2026-11-19", "2026-11-21", SeekerTripStates.OnTrip)]
    [InlineData("2026-11-22", "2026-11-25", SeekerTripStates.InProgress)]
    [InlineData(null, null, SeekerTripStates.InProgress)]
    public void Trip_card_state_by_dates(string? start, string? end, string expected)
        => Assert.Equal(expected, SeekerTripStates.Of(start is null ? null : DateOnly.Parse(start), end is null ? null : DateOnly.Parse(end), Be4Facts.Today));

    [Fact]
    public void Chosen_trip_is_the_soonest_not_ended_one_dates_first()
    {
        var ended = new T(Guid.NewGuid(), new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 3));
        var noDates = new T(Guid.NewGuid(), null, null);
        var later = new T(Guid.NewGuid(), new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 22));
        var soon = new T(Guid.NewGuid(), new DateOnly(2026, 11, 26), new DateOnly(2026, 11, 29));
        Assert.Equal(soon, SeekerStatusRules.Choose([ended, noDates, later, soon], t => t.Start, t => t.End, t => t.Id, Be4Facts.Today));
        Assert.Equal(noDates, SeekerStatusRules.Choose([ended, noDates], t => t.Start, t => t.End, t => t.Id, Be4Facts.Today));
        Assert.Null(SeekerStatusRules.Choose([ended], t => t.Start, t => t.End, t => t.Id, Be4Facts.Today));
    }

    [Fact]
    public void Today_block_lists_the_day_places_and_never_a_seen_count()
    {
        var trip = new Trip { Id = Guid.NewGuid(), Title = "Nainital trip", CityName = "Nainital", StartDate = new DateOnly(2026, 11, 20), EndDate = new DateOnly(2026, 11, 23) };
        var plan = new TripPlan
        {
            Days =
            [
                new TripDay { Day = 1, Spots = [new TripSpot { Id = "a", Name = "Naini Lake" }] },
                new TripDay { Day = 2, Spots = [new TripSpot { Id = "b", Name = "Snow View" }, new TripSpot { Id = "c", Name = "Mall Road" }] },
            ],
        };
        var today = SeekerFeedReader.Today(plan, trip, trip.StartDate!.Value, Be4Facts.Today)!;
        Assert.Equal(2, today.DayNumber);
        Assert.Equal(4, today.TotalDays);
        Assert.Equal(2, today.PlaceCount);
        Assert.Equal(["Snow View", "Mall Road"], today.Places.Select(p => p.Name));
        Assert.Null(today.PlacesSeen);
        Assert.Equal("Nainital", today.CityName);
    }

    [Fact]
    public void Status_reads_only_stay_enquiries()
    {
        var tripId = Guid.NewGuid();
        var rows = new List<TripEnquiry>
        {
            new() { Id = Guid.NewGuid(), Kind = TripEnquiryKinds.Taxi, Status = TripEnquiryStatuses.Offered, ListingName = "Taxi", UpdatedAt = Be4Facts.Now },
            new() { Id = Guid.NewGuid(), Kind = TripEnquiryKinds.Stay, Status = TripEnquiryStatuses.ConfirmedByBoth, ListingName = "Lake View", UpdatedAt = Be4Facts.Now },
        };
        var s = SeekerFeedReader.Status(tripId, rows)!;
        Assert.Equal(SeekerStatusKinds.Confirmed, s.Kind);
        Assert.Equal(tripId, s.TripId);
    }

    private sealed record T(Guid Id, DateOnly? Start, DateOnly? End);
}

// AU-1 / AU-2 / AU-3: every blocker carries its button (action, route, args), side and reason key; old constructions keep working.
public class AccountDeletionBlockMappingTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly Guid Trip = Guid.NewGuid();

    private static AccountDeletionBlock B(string code, string side, bool lead = false, string? kind = null, int count = 1)
        => AccountDeletionRules.Block(new AccountActivityItem(code, side, Id, count, Be4Facts.Now, Trip, lead, kind));

    private static JsonElement Args(AccountDeletionBlock b) => b.Args!.Value;

    [Fact]
    public void Host_booking_opens_the_confirmed_enquiry()
    {
        var b = B(AccountDeletionReasons.ActiveStayBooking, AccountDeletionSides.Host, count: 2);
        Assert.Equal((AccountDeletionActions.OpenEnquiry, UserNotificationRoutes.StayEnquiries, Id), (b.Action, b.Route, b.TargetId!.Value));
        Assert.Equal("Confirmed", Args(b).GetProperty("bucket").GetString());
        Assert.Equal(2, Args(b).GetProperty("count").GetInt32());
        Assert.Equal("deleteBlock.ActiveStayBooking", b.ReasonKey);
    }

    [Fact]
    public void Traveller_booking_and_enquiry_open_trip_replies_with_the_trip_id()
    {
        foreach (var code in new[] { AccountDeletionReasons.ActiveStayBooking, AccountDeletionReasons.OpenStayEnquiry })
        {
            var b = B(code, AccountDeletionSides.Traveller);
            Assert.Equal((AccountDeletionActions.OpenTripReplies, UserNotificationRoutes.TripReplies, Trip), (b.Action, b.Route, b.TargetId!.Value));
            Assert.Equal(Id.ToString(), Args(b).GetProperty("enquiryId").GetString());
        }
    }

    [Fact]
    public void Host_open_enquiry_opens_the_new_bucket()
        => Assert.Equal("New", Args(B(AccountDeletionReasons.OpenStayEnquiry, AccountDeletionSides.Host)).GetProperty("bucket").GetString());

    [Fact]
    public void Taxi_rows_route_by_side_and_lead()
    {
        Assert.Equal(UserNotificationRoutes.TaxiBookings, B(AccountDeletionReasons.ActiveTaxiBooking, AccountDeletionSides.Driver).Route);
        Assert.Equal(UserNotificationRoutes.TripLeads, B(AccountDeletionReasons.ActiveTaxiBooking, AccountDeletionSides.Driver, lead: true).Route);
        var traveller = B(AccountDeletionReasons.ActiveTaxiBooking, AccountDeletionSides.Traveller);
        Assert.Equal((AccountDeletionActions.OpenTaxiStatus, UserNotificationRoutes.TripTaxiStatus), (traveller.Action, traveller.Route));
        var lead = B(AccountDeletionReasons.ActiveTaxiBooking, AccountDeletionSides.Traveller, lead: true);
        Assert.Equal((UserNotificationRoutes.TripPlan, Trip), (lead.Route, lead.TargetId!.Value));
        var offer = B(AccountDeletionReasons.SelectedTaxiOffer, AccountDeletionSides.Driver);
        Assert.Equal((AccountDeletionActions.OpenTaxiOffer, UserNotificationRoutes.TaxiRequestDetail), (offer.Action, offer.Route));
        var request = B(AccountDeletionReasons.OpenTaxiRequest, AccountDeletionSides.Traveller);
        Assert.Equal((AccountDeletionActions.OpenTaxiStatus, Id), (request.Action, request.TargetId!.Value));
        var leadRequest = B(AccountDeletionReasons.OpenTaxiRequest, AccountDeletionSides.Traveller, lead: true);
        Assert.Equal((AccountDeletionActions.OpenTrip, Trip), (leadRequest.Action, leadRequest.TargetId!.Value));
    }

    [Fact]
    public void Live_listing_means_go_offline_on_home()
    {
        var b = B(AccountDeletionReasons.LiveListing, AccountDeletionSides.Host, kind: ListingKinds.Stay);
        Assert.Equal((AccountDeletionActions.GoOffline, UserNotificationRoutes.OwnerHome), (b.Action, b.Route));
        Assert.Equal("home", Args(b).GetProperty("tab").GetString());
        Assert.Equal("Stay", Args(b).GetProperty("listingKind").GetString());
        Assert.Equal("Take your listing offline first.", b.Message);
    }

    [Fact]
    public void Rows_merge_per_kind_and_side_and_keep_the_table_order()
    {
        var older = new AccountActivityItem(AccountDeletionReasons.ActiveTaxiBooking, AccountDeletionSides.Driver, Guid.NewGuid(), 2, Be4Facts.Now.AddDays(-2));
        var newer = new AccountActivityItem(AccountDeletionReasons.ActiveTaxiBooking, AccountDeletionSides.Driver, Guid.NewGuid(), 1, Be4Facts.Now, null, true);
        var live = new AccountActivityItem(AccountDeletionReasons.LiveListing, AccountDeletionSides.Driver, Guid.NewGuid(), 1, Be4Facts.Now, null, false, "Taxi");
        var stay = new AccountActivityItem(AccountDeletionReasons.ActiveStayBooking, AccountDeletionSides.Traveller, Guid.NewGuid(), 1, Be4Facts.Now, Trip);
        var rows = AccountDeletionRules.Blocks([live, newer, stay, older]);
        Assert.Equal([AccountDeletionReasons.ActiveTaxiBooking, AccountDeletionReasons.ActiveStayBooking, AccountDeletionReasons.LiveListing], rows.Select(r => r.Code));
        Assert.Equal(older.Id, rows[0].TargetId);
        Assert.Equal(3, rows[0].Args!.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public void An_old_two_argument_block_still_serialises_the_two_old_fields()
    {
        var json = JsonSerializer.Serialize(new AccountDeletionBlock("X", "Y"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("X", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("Y", doc.RootElement.GetProperty("message").GetString());
        Assert.False(doc.RootElement.TryGetProperty("argsJson", out _));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("args").ValueKind);
    }

    [Fact]
    public void Every_reason_code_has_a_message()
        => Assert.All(AccountDeletionReasons.All, c => Assert.True(AccountDeletionReasons.Messages.ContainsKey(c)));
}

// Section 9 A1, R-14: plan key catalog for the admin form and the value rules (range, kind, Plus >= Free).
public class AdminPlanValueTests
{
    [Fact]
    public void Keys_cover_every_plan_key_with_per_kind_labels()
    {
        var keys = AdminPlanKeyHandlers.Keys();
        Assert.Equal(PlanFeatureKeys.All, keys.Select(k => k.Key).ToArray());
        var calendar = keys.Single(k => k.Key == PlanFeatureKeys.CalendarEntries);
        Assert.Equal("Bookings he adds, active", calendar.Labels[ListingKinds.Stay]);
        Assert.Equal("Trips he adds, active", calendar.Labels[ListingKinds.Taxi]);
        Assert.Equal(["Taxi"], keys.Single(k => k.Key == PlanFeatureKeys.Routes).ListingKinds);
    }

    [Theory]
    [InlineData("Stay", "Photos", 12, true)]
    [InlineData("Stay", "Photos", 13, false)]
    [InlineData("Stay", "Photos", 0, false)]
    [InlineData("Stay", "Routes", 3, false)]
    [InlineData("Taxi", "Routes", 10, true)]
    [InlineData("Taxi", "RingWeight", 6, false)]
    [InlineData("Stay", "MonthSummary", 1, true)]
    [InlineData("Stay", "MonthSummary", 2, false)]
    [InlineData("Stay", "Nope", 1, false)]
    public void Value_must_fit_the_key_and_the_kind(string kind, string key, int value, bool ok)
        => Assert.Equal(ok, PlanValueRules.KeyError(kind, key, value) is null);

    [Fact]
    public void Plus_below_free_is_refused_for_limit_keys_only()
    {
        var free = PlanFeatureValues.From(OwnerPlanCatalog.Features(ListingKinds.Stay, false));
        var plusFeatures = OwnerPlanCatalog.Features(ListingKinds.Stay, true);
        Assert.Null(PlanValueRules.BelowFree(ListingKinds.Stay, free, PlanFeatureValues.From(plusFeatures)));
        plusFeatures[PlanFeatureKeys.Photos] = 1;
        var hit = PlanValueRules.BelowFree(ListingKinds.Stay, free, PlanFeatureValues.From(plusFeatures))!;
        Assert.Equal((PlanFeatureKeys.Photos, 2, 1), (hit.Key, hit.Free, hit.Plus));
        var ranking = OwnerPlanCatalog.Features(ListingKinds.Stay, true);
        ranking[PlanFeatureKeys.Priority] = 0;
        Assert.Null(PlanValueRules.BelowFree(ListingKinds.Stay, PlanFeatureValues.From(OwnerPlanCatalog.Features(ListingKinds.Stay, false)) with { Priority = 1 },
            PlanFeatureValues.From(ranking)));
    }

    [Fact]
    public void A_key_left_out_keeps_its_stored_value()
    {
        var merged = PlanValueRules.Merge(new Dictionary<string, int> { ["Photos"] = 8, ["RoomTypes"] = 6 }, [("Photos", 10)]);
        Assert.Equal(10, merged["Photos"]);
        Assert.Equal(6, merged["RoomTypes"]);
    }

    [Fact]
    public void Wire_values_list_every_key_of_the_kind_with_switches_as_bool()
    {
        var values = PlanValueRules.WireValues(ListingKinds.Taxi, PlanFeatureValues.From(new Dictionary<string, int>()));
        Assert.Equal(PlanKeyCatalog.For(ListingKinds.Taxi).Count(), values.Count);
        Assert.IsType<bool>(values[PlanFeatureKeys.Reminders]);
        Assert.Equal(1, values[PlanFeatureKeys.Routes]);
        Assert.False(values.ContainsKey(PlanFeatureKeys.Photos));
    }

    [Fact]
    public void Plan_state_line_for_the_review_screen()
    {
        var off = PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), new PaymentFeatureState(false, 30, null, PaymentProviders.Play), ListingKinds.Stay, null);
        var on = PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), new PaymentFeatureState(true, 30, null, PaymentProviders.Play), ListingKinds.Stay, null);
        Assert.Equal("Free · Plus ended", AdminPlanStateRules.Line(on, 2));
        Assert.Equal("Free", AdminPlanStateRules.Line(on, 0));
        Assert.EndsWith("payment off", AdminPlanStateRules.Line(off, 0));
    }
}

public class OwnerHomeRoleTests
{
    [Theory]
    [InlineData("STAY_OWNER", "Stay")]
    [InlineData("TAXI_DRIVER", "Taxi")]
    [InlineData("SEEKER", null)]
    [InlineData(null, null)]
    public void Home_is_for_owner_roles_only(string? role, string? kind)
        => Assert.Equal(kind, OwnerHomeHandlers.KindOf(role));

    [Fact]
    public void Notice_viewer_kind_never_throws()
    {
        Assert.Equal(ListingKinds.Taxi, RentNearBy.Infrastructure.Services.Buddy.Be4.NoticeReader.ViewerKind(UserRoles.TaxiDriverId));
        Assert.Equal(ListingKinds.Stay, RentNearBy.Infrastructure.Services.Buddy.Be4.NoticeReader.ViewerKind(UserRoles.StayOwnerId));
        Assert.Null(RentNearBy.Infrastructure.Services.Buddy.Be4.NoticeReader.ViewerKind(Guid.NewGuid()));
        Assert.Null(RentNearBy.Infrastructure.Services.Buddy.Be4.NoticeReader.ViewerKind(null));
    }
}
