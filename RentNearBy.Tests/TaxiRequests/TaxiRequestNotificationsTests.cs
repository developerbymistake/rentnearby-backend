using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiRequestNotificationsTests
{
    private static readonly Guid Trip = Guid.Parse("7b1c2f0e-5a44-4c1e-9d3a-0f6f2a1b9c01");
    private static readonly Guid Request = Guid.Parse("3f9a6c2d-1b7e-4f0a-8c55-2e7d9b4a6101");
    private static readonly Guid Offer = Guid.Parse("a1b2c3d4-0001-4a00-8000-000000000001");
    private static readonly string TravellerArgs = $"{{\"tripId\":\"{Trip}\",\"requestId\":\"{Request}\"}}";

    private static TaxiNoticeCopy Copy(string type, string? name = null, TaxiNoticeDetail? detail = null)
        => TaxiRequestNotifications.Build(type, Trip, Request, Offer, name, detail);

    private sealed record Row(string Type, string Wire, string Title, string Body, string Route, string Args, string? Name = null, TaxiNoticeDetail? Detail = null);

    [Fact]
    public void Every_notice_matches_the_contract_table()
    {
        var offerArgs = $"{{\"offerId\":\"{Offer}\"}}";
        var rows = new Row[]
        {
            new(NotificationTypes.TaxiDriverAccepted, "taxi_driver_accepted", "A driver accepted", "Open to call and choose your driver", "/trip-taxi-status", TravellerArgs),
            new(NotificationTypes.TaxiSelectionLapsed, "taxi_selection_lapsed", "Driver did not confirm", "Choose another driver", "/trip-taxi-status", TravellerArgs),
            new(NotificationTypes.TaxiBooked, "taxi_booked", "Taxi booked", "Ravi confirmed your booking", "/trip-taxi-status", TravellerArgs, "Ravi"),
            new(NotificationTypes.TaxiRequestEnded, "taxi_request_ended", "All drivers are busy right now", "Please try again in a little while.", "/trip-taxi-status", TravellerArgs, null, new(RequestEndReasons.AllDriversBusy)),
            new(NotificationTypes.TaxiRequestEnded, "taxi_request_ended", "All drivers are busy right now", "Please try again in a little while.", "/trip-taxi-status", TravellerArgs, null, new(RequestEndReasons.NotBooked)),
            new(NotificationTypes.TaxiRequestEnded, "taxi_request_ended", "No driver is ready right now", "Please try again in a little while.", "/trip-taxi-status", TravellerArgs, null, new(RequestEndReasons.NoReadyDriver)),
            new(NotificationTypes.TaxiRequestEnded, "taxi_request_ended", "Request closed", "You can send a new request", "/trip-taxi-status", TravellerArgs, null, new(RequestEndReasons.AdminCancelled)),
            new(NotificationTypes.TaxiDriverWithdrew, "taxi_driver_withdrew", "Your driver cancelled", "We are finding another driver", "/trip-taxi-status", TravellerArgs),
            new(NotificationTypes.TaxiDriverWithdrew, "taxi_driver_withdrew", "Your driver cancelled", "You can request another taxi", "/trip-taxi-status", TravellerArgs, null, new(DuringTrip: true)),
            new(NotificationTypes.TaxiRateDriver, "taxi_rate_driver", "Rate your driver", "How was your trip with Ravi?", "/trip-taxi-status", TravellerArgs, "Ravi"),
            new(NotificationTypes.TaxiTripEnded, "taxi_trip_ended", "Trip ended", "Ravi ended the trip", "/trip-taxi-status", TravellerArgs, "Ravi"),
            new(NotificationTypes.TaxiTripEnded, "taxi_trip_ended", "Trip ended", "Aarav ended the trip", "/taxi-bookings", "{}", "Aarav", new(ToDriver: true)),
            new(NotificationTypes.TaxiSelected, "taxi_selected", "Traveller selected you", "Confirm within 15 minutes", "/taxi-request-detail", offerArgs),
            new(NotificationTypes.TaxiNotSelected, "taxi_not_selected", "Not selected", "The traveller chose another driver", "/taxi-requests", "{}"),
            new(NotificationTypes.TaxiRequestClosed, "taxi_request_closed", "Request closed", "The traveller cancelled the request", "/taxi-requests", "{}", null, new(RequestEndReasons.TravellerCancelled)),
            new(NotificationTypes.TaxiRequestClosed, "taxi_request_closed", "Request closed", "This request is closed", "/taxi-requests", "{}", null, new(RequestEndReasons.NotBooked)),
            new(NotificationTypes.TaxiRequestClosed, "taxi_request_closed", "Request closed", "This request is closed", "/taxi-requests", "{}"),
            new(NotificationTypes.TaxiBookingCancelled, "taxi_booking_cancelled", "Booking cancelled", "Your dates are free again", "/taxi-bookings", "{}"),
            new(NotificationTypes.TaxiRingsMissed, "taxi_rings_missed", "You are missing requests", "Open Get ready for requests to fix your phone", "/taxi-ready", "{}"),
            new(NotificationTypes.TaxiChooseDriver, "taxi_choose_driver", "Choose your driver", "3 drivers replied. Choose one to confirm your taxi.", "/trip-taxi-status", TravellerArgs, null, new(Count: 3)),
            new(NotificationTypes.TaxiChooseDriver, "taxi_choose_driver", "Choose your driver", "1 driver replied. Choose the driver to confirm your taxi.", "/trip-taxi-status", TravellerArgs, null, new(Count: 1)),
        };

        foreach (var r in rows)
        {
            var copy = Copy(r.Type, r.Name, r.Detail);
            Assert.Equal(r.Type, copy.Type);
            Assert.Equal(r.Wire, NotificationTypes.ToWireValue(copy.Type));
            Assert.Equal(r.Title, copy.Title);
            Assert.Equal(r.Body, copy.Body);
            Assert.Equal(r.Route, copy.Route);
            Assert.Equal(r.Args, copy.ArgsJson);
        }
        Assert.Equal(13, rows.Select(r => r.Type).Distinct().Count());
        Assert.Equal(TaxiRequestNotifications.Types.OrderBy(t => t), rows.Select(r => r.Type).Distinct().OrderBy(t => t));
    }

    [Fact]
    public void Every_notice_kind_maps_to_a_listed_type()
    {
        foreach (var kind in Enum.GetValues<TaxiNoticeKind>())
            Assert.Contains(TaxiNoticeTypes.TypeOf(kind), TaxiRequestNotifications.Types);
    }

    [Fact]
    public void Missing_names_use_a_plain_fallback()
    {
        Assert.Equal("Your driver confirmed your booking", Copy(NotificationTypes.TaxiBooked, " ").Body);
        Assert.Equal("The traveller ended the trip", Copy(NotificationTypes.TaxiTripEnded, null, new(ToDriver: true)).Body);
    }

    [Fact]
    public void Selected_needs_an_offer_id()
        => Assert.Throws<ArgumentException>(() => TaxiRequestNotifications.Build(NotificationTypes.TaxiSelected, Trip, Request));

    [Fact]
    public async Task Writer_adds_rows_and_never_saves()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite()).Options);
        var writer = new TaxiPushWriter(new TaxiAckTokens("test-signing-key-test-signing-key-123"));
        var driver = Guid.NewGuid();

        await writer.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.DriverAccepted, Guid.NewGuid(), Trip, Request));
        await writer.RingEndAsync(db, new RingEndPush(Offer, Request, driver, "Filled"));
        await writer.RingEndAsync(db, new RingEndPush(Guid.NewGuid(), Request, driver, "Expired"));

        db.Set<RentNearBy.Core.Entities.TaxiRequests.TaxiRequest>().Add(new()
        {
            Id = Request, TripId = Trip, TravellerUserId = Guid.NewGuid(), DateFrom = new DateOnly(2026, 12, 18), DateTo = new DateOnly(2026, 12, 21),
            PickupCityName = "Kathgodam", WantPickup = true, WantSightseeing = true, WantDrop = true, Seats = 4, Travellers = 2,
            SightseeingDaysJson = "[2,3,4]", TripSnapshotJson = "{\"cityNames\":[\"Nainital\",\"Ranikhet\"]}",
        });
        await writer.RingAsync(db, new RingPush(Offer, Request, driver, new DateTime(2026, 12, 10, 9, 46, 7, DateTimeKind.Utc)));
        var ring = System.Text.Json.JsonSerializer.Deserialize<OutboxPushPayload>(
            db.ChangeTracker.Entries<OutboxMessage>().Single(e => e.Entity.Key == TaxiPushKinds.RingKey(Offer)).Entity.Payload,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(driver, ring.UserId);
        Assert.Equal(NotificationTypes.TaxiRequestRing, ring.Kind);
        Assert.Equal(new DateTime(2026, 12, 10, 9, 46, 7, DateTimeKind.Utc), ring.ExpiresAtUtc);
        Assert.Equal("Kathgodam to Nainital, Ranikhet", ring.Data!["line2"]);
        Assert.Equal("3", ring.Data["sightseeingDays"]);
        Assert.True(new TaxiAckTokens("test-signing-key-test-signing-key-123").IsValid(Offer, ring.Data["ackToken"]));

        Assert.Single(db.ChangeTracker.Entries<NotificationEvent>());
        Assert.Equal(3, db.ChangeTracker.Entries<OutboxMessage>().Count());
        Assert.All(db.ChangeTracker.Entries(), e => Assert.Equal(EntityState.Added, e.State));
    }

    private static readonly Guid Traveller = Guid.Parse("c0ffee00-0000-4000-8000-000000000001");
    private static readonly Guid Driver = Guid.Parse("c0ffee00-0000-4000-8000-000000000002");

    public static TheoryData<TaxiNoticeKind, Guid, string?, string, string, string> InterfaceVariants => new()
    {
        { TaxiNoticeKind.RequestEnded, Traveller, RequestEndReasons.AllDriversBusy, "All drivers are busy right now", "Please try again in a little while.", "/trip-taxi-status" },
        { TaxiNoticeKind.RequestEnded, Traveller, RequestEndReasons.NotBooked, "All drivers are busy right now", "Please try again in a little while.", "/trip-taxi-status" },
        { TaxiNoticeKind.RequestEnded, Traveller, RequestEndReasons.NoReadyDriver, "No driver is ready right now", "Please try again in a little while.", "/trip-taxi-status" },
        { TaxiNoticeKind.RequestClosed, Driver, RequestEndReasons.NoReadyDriver, "Request closed", "This request is closed", "/taxi-requests" },
        { TaxiNoticeKind.RequestEnded, Traveller, RequestEndReasons.AdminCancelled, "Request closed", "You can send a new request", "/trip-taxi-status" },
        { TaxiNoticeKind.DriverWithdrew, Traveller, RequestEndReasons.DriverLeftTrip, "Your driver cancelled", "You can request another taxi", "/trip-taxi-status" },
        { TaxiNoticeKind.DriverWithdrew, Traveller, null, "Your driver cancelled", "We are finding another driver", "/trip-taxi-status" },
        { TaxiNoticeKind.TripEnded, Driver, null, "Trip ended", "Aarav ended the trip", "/taxi-bookings" },
        { TaxiNoticeKind.TripEnded, Traveller, null, "Trip ended", "Aarav ended the trip", "/trip-taxi-status" },
        { TaxiNoticeKind.RequestClosed, Driver, RequestEndReasons.TravellerCancelled, "Request closed", "The traveller cancelled the request", "/taxi-requests" },
        { TaxiNoticeKind.RequestClosed, Driver, RequestEndReasons.BookingCancelledByTraveller, "Request closed", "The traveller cancelled the request", "/taxi-requests" },
        { TaxiNoticeKind.RequestClosed, Driver, RequestEndReasons.AllDriversBusy, "Request closed", "This request is closed", "/taxi-requests" },
        { TaxiNoticeKind.RequestClosed, Driver, RequestEndReasons.AdminCancelled, "Request closed", "This request is closed", "/taxi-requests" },
        { TaxiNoticeKind.RequestClosed, Driver, RequestEndReasons.AccountClosed, "Request closed", "This request is closed", "/taxi-requests" },
    };

    [Theory]
    [MemberData(nameof(InterfaceVariants))]
    public async Task Interface_notify_picks_the_variant_from_the_request(TaxiNoticeKind kind, Guid recipient, string? endReason, string title, string body, string route)
    {
        await using var db = DesignDb();
        db.Set<RentNearBy.Core.Entities.TaxiRequests.TaxiRequest>().Add(new()
        {
            Id = Request, TripId = Trip, TravellerUserId = Traveller, EndReason = endReason,
            DateFrom = new DateOnly(2026, 12, 18), DateTo = new DateOnly(2026, 12, 21),
        });
        ITaxiPushWriter writer = new TaxiPushWriter(new TaxiAckTokens("test-signing-key-test-signing-key-123"));

        await writer.NotifyAsync(db, new TaxiNotice(kind, recipient, Trip, Request, null, "Aarav"));

        var ev = db.ChangeTracker.Entries<NotificationEvent>().Single().Entity;
        Assert.Equal(recipient, ev.TargetUserId);
        Assert.Equal(title, ev.Title);
        Assert.Equal(body, ev.Body);
        Assert.Equal(route, ev.ActionRoute);
        Assert.Equal(route is "/taxi-bookings" or "/taxi-requests" ? "{}" : TravellerArgs, ev.ActionArgumentsJson);
    }

    [Fact]
    public void Detail_comes_only_for_the_four_variant_kinds()
    {
        var facts = new TaxiPushWriter.NoticeFacts(Traveller, RequestEndReasons.DriverLeftTrip);
        foreach (var kind in Enum.GetValues<TaxiNoticeKind>())
        {
            var detail = TaxiPushWriter.DetailOf(new TaxiNotice(kind, Driver, Trip, Request, Offer), facts);
            Assert.Equal(kind is TaxiNoticeKind.RequestEnded or TaxiNoticeKind.RequestClosed or TaxiNoticeKind.DriverWithdrew or TaxiNoticeKind.TripEnded, detail is not null);
        }
    }

    // O8: a selection that lapsed because the driver is booked elsewhere (or his taxi is gone) tells the traveller so.
    [Theory]
    [InlineData(OfferEndReasons.Booked, "Driver not available", "This driver is no longer available. Choose another driver.")]
    [InlineData(OfferEndReasons.ListingRemoved, "Driver not available", "This driver is no longer available. Choose another driver.")]
    [InlineData(OfferEndReasons.ConfirmTimeout, "Driver did not confirm", "Choose another driver")]
    [InlineData(OfferEndReasons.DeclinedBooking, "Driver did not confirm", "Choose another driver")]
    [InlineData(null, "Driver did not confirm", "Choose another driver")]
    public async Task Selection_lapsed_copy_follows_the_offer_end_reason(string? reason, string title, string body)
    {
        await using var db = DesignDb();
        ITaxiPushWriter writer = new TaxiPushWriter(new TaxiAckTokens("test-signing-key-test-signing-key-123"));

        await writer.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.SelectionLapsed, Traveller, Trip, Request, Offer, Reason: reason));

        var ev = db.ChangeTracker.Entries<NotificationEvent>().Single().Entity;
        Assert.Equal((NotificationTypes.TaxiSelectionLapsed, title, body, "/trip-taxi-status", TravellerArgs),
            (ev.Type, ev.Title, ev.Body, ev.ActionRoute, ev.ActionArgumentsJson));
    }

    // O11 + O14: the driver's answer on one device ends the ring on his other devices.
    [Theory]
    [InlineData(RingEndReasons.Accepted, "You accepted this request")]
    [InlineData(RingEndReasons.Declined, "You declined this request")]
    public async Task Answer_ring_end_goes_to_the_driver_with_the_ring_tag(string reason, string body)
    {
        await using var db = DesignDb();
        await new TaxiPushWriter(new TaxiAckTokens("test-signing-key-test-signing-key-123"))
            .RingEndAsync(db, new RingEndPush(Offer, Request, Driver, reason));
        var row = db.ChangeTracker.Entries<OutboxMessage>().Single().Entity;
        var push = System.Text.Json.JsonSerializer.Deserialize<OutboxPushPayload>(row.Payload,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(TaxiPushKinds.RingEndKey(Offer), row.Key);
        Assert.Equal((Driver, body, TaxiPushKinds.RingTag(Offer), NotificationTypes.TaxiRequestRingEnded), (push.UserId, push.Body, push.Tag, push.Kind));
        Assert.Equal((TaxiPushKinds.RingEnd, reason), (push.Data!["kind"], push.Data["reason"]));
        Assert.Null(push.ExceptToken);
    }

    [Fact]
    public async Task Answer_ring_end_carries_the_answering_token_to_skip()
    {
        await using var db = DesignDb();
        await new TaxiPushWriter(new TaxiAckTokens("test-signing-key-test-signing-key-123"))
            .RingEndAsync(db, new RingEndPush(Offer, Request, Driver, RingEndReasons.Accepted, "answering-token"));
        var row = db.ChangeTracker.Entries<OutboxMessage>().Single().Entity;
        var push = System.Text.Json.JsonSerializer.Deserialize<OutboxPushPayload>(row.Payload,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("answering-token", push.ExceptToken);
        Assert.DoesNotContain(push.Data!.Values, v => v == "answering-token");
    }

    [Theory]
    [InlineData("{\"cities\":[{\"name\":\"Nainital\",\"nights\":3,\"from\":\"2026-12-18\",\"to\":\"2026-12-21\"},{\"name\":\"Ranikhet\",\"nights\":1,\"from\":\"2026-12-21\",\"to\":\"2026-12-22\"}],\"days\":[]}")]
    [InlineData("{\"Cities\":[{\"Name\":\"Nainital\",\"Nights\":3},{\"Name\":\"Ranikhet\",\"Nights\":1}]}")]
    [InlineData("{\"cityNames\":[\"Nainital\",\" Ranikhet \"]}")]
    [InlineData("{\"cities\":[\"Nainital\",\"Ranikhet\",\"\"]}")]
    public void City_names_read_every_snapshot_shape(string json)
        => Assert.Equal(["Nainital", "Ranikhet"], TaxiPushWriter.CityNames(json));

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"cities\":{\"name\":\"Nainital\"}}")]
    public void City_names_are_empty_for_a_missing_or_bad_snapshot(string json)
        => Assert.Empty(TaxiPushWriter.CityNames(json));

    [Fact]
    public async Task Ring_line2_uses_the_contract_cities_shape()
    {
        await using var db = DesignDb();
        db.Set<RentNearBy.Core.Entities.TaxiRequests.TaxiRequest>().Add(new()
        {
            Id = Request, TripId = Trip, TravellerUserId = Traveller, DateFrom = new DateOnly(2026, 12, 18), DateTo = new DateOnly(2026, 12, 21),
            PickupCityName = "Kathgodam", WantPickup = true, Seats = 4, Travellers = 2,
            TripSnapshotJson = "{\"cities\":[{\"name\":\"Nainital\",\"nights\":3,\"from\":\"2026-12-18\",\"to\":\"2026-12-21\"},{\"name\":\"Ranikhet\",\"nights\":1,\"from\":\"2026-12-21\",\"to\":\"2026-12-22\"}]}",
        });
        await new TaxiPushWriter(new TaxiAckTokens("test-signing-key-test-signing-key-123"))
            .RingAsync(db, new RingPush(Offer, Request, Driver, new DateTime(2026, 12, 10, 9, 46, 7, DateTimeKind.Utc)));
        var ring = System.Text.Json.JsonSerializer.Deserialize<OutboxPushPayload>(
            db.ChangeTracker.Entries<OutboxMessage>().Single().Entity.Payload,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("Kathgodam to Nainital, Ranikhet", ring.Data!["line2"]);
    }

    private static ApplicationDbContext DesignDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite()).Options);
}
