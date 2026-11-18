using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Infrastructure.Data;

using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class AdminTaxiRequestTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Created = new(2026, 12, 10, 9, 41, 7, DateTimeKind.Utc);
    private static readonly Guid RequestId = Guid.Parse("3f9a6c2d-1b7e-4f0a-8c55-2e7d9b4a6101");

    private static JsonNode Sample(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, name)))!;

    private static void AssertEqualsSample(string name, object dto)
    {
        var written = JsonSerializer.SerializeToNode(dto, dto.GetType(), Web)!;
        Assert.True(JsonNode.DeepEquals(Sample(name), written), $"{name}\nexpected {Sample(name).ToJsonString()}\nactual   {written.ToJsonString()}");
    }

    private static TaxiRequest Request() => new()
    {
        Id = RequestId,
        TripId = Guid.Parse("7b1c2f0e-5a44-4c1e-9d3a-0f6f2a1b9c01"),
        TravellerUserId = Guid.NewGuid(),
        Status = RequestStates.Collecting,
        WantPickup = true, WantSightseeing = true, WantDrop = true,
        PickupCityName = "Kathgodam", DropCityName = "Kathgodam",
        Seats = 4, Travellers = 2,
        DateFrom = new DateOnly(2026, 12, 18), DateTo = new DateOnly(2026, 12, 21),
        CreatedAt = Created, UpdatedAt = Created,
    };

    private static TaxiRequestOffer Offer() => new()
    {
        Id = Guid.Parse("a1b2c3d4-0001-4a00-8000-000000000001"),
        RequestId = RequestId,
        DriverListingId = Guid.Parse("d0d0d0d0-0001-4d00-8000-000000000001"),
        Status = OfferStates.Accepted,
        OfferedAt = Created.AddSeconds(1), ExpiresAt = Created.AddMinutes(5).AddSeconds(1),
        PushReceivedAt = Created.AddSeconds(3), RespondedAt = Created.AddSeconds(84),
    };

    private static TaxiBooking Booking() => new()
    {
        Id = Guid.Parse("b00c1e55-0001-4b00-8000-000000000001"),
        RequestId = RequestId,
        Status = BookingStates.Booked,
        DriverName = "Ramesh Kumar", TravellerName = "Aarav Sharma", PickupCityName = "Kathgodam",
        DateFrom = new DateOnly(2026, 12, 18), DateTo = new DateOnly(2026, 12, 21),
        BookedAt = new DateTime(2026, 12, 10, 9, 52, 12, DateTimeKind.Utc),
    };

    [Fact]
    public void List_detail_bookings_health_dtos_equal_the_samples()
    {
        var now = Created.AddMinutes(4).AddSeconds(30);
        var item = AdminTaxiRequestRules.ToItem(Request(), "Uttarakhand Hills Trip", "Aarav Sharma", "+919876543210",
            AdminTaxiRequestRules.CountsOf([OfferStates.Ringing, OfferStates.Accepted, OfferStates.Selected]), now);
        AssertEqualsSample("admin_taxi_request_list.json", new AdminTaxiRequestListDto { Items = [item] });

        var offer = AdminTaxiRequestRules.ToOffer(Offer(), "Ramesh Kumar", "+919876500001");
        var due = new DueAction { Kind = DueActionKinds.RequestStaleExpire, State = DueActionStates.Pending, DueAt = new DateTime(2026, 12, 12, 9, 41, 7, DateTimeKind.Utc), Attempts = 0 };
        var detail = AdminTaxiRequestRules.ToDetail(Request(), "Uttarakhand Hills Trip", "Aarav Sharma", "+919876543210",
            [offer], null, [AdminTaxiRequestRules.ToDueAction(due)]);
        AssertEqualsSample("admin_taxi_request_detail.json", detail);

        var booking = AdminTaxiRequestRules.ToBooking(Booking(), "+919876500001", "+919876543210", null);
        AssertEqualsSample("admin_taxi_bookings.json", new AdminTaxiBookingListDto { Items = [booking] });

        AssertEqualsSample("admin_taxi_health.json", new AdminTaxiHealthDto
        {
            SwitchOn = true, RequestsToday = 18,
            ByState = new AdminTaxiByStateDto { Open = 2, Collecting = 3, Selected = 1, Booked = 6, InProgress = 1, Completed = 3, Cancelled = 1, Expired = 1 },
            StuckRequests = 0, PendingDueActions = 14, OldestDueActionSeconds = 0, OutboxPending = 0, OldestOutboxSeconds = 0,
            RingsSentLastHour = 21, RingsAckedLastHour = 20, AckRatioLastHour = 0.95, AckP95SecondsLastHour = 2.4,
            DriversRingReady = 37, DriversMissingRings = 1, BookedThisWeek = 7, OffersAnsweredPct7d = 82, RoutesWithoutCity = 2,
            WorkerHeartbeatSeconds = new AdminTaxiHeartbeatsDto { DueActions = 3, Matcher = 1, OutboxRelay = 1 },
        });
    }

    [Fact]
    public void Offer_counts_group_the_states()
    {
        var c = AdminTaxiRequestRules.CountsOf([OfferStates.Ringing, OfferStates.Accepted, OfferStates.Confirmed, OfferStates.Missed, OfferStates.Declined]);
        Assert.Equal((5, 1, 2, 1), (c.Total, c.Ringing, c.Accepted, c.Missed));
    }

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    [Fact]
    public async Task Cancel_note_must_be_1_to_300()
    {
        // A bad note is refused before the service touches any dependency.
        var service = new TaxiRequestService(null!, null!, null!, null!, null!, null!, null!);
        foreach (var note in new[] { "", "   ", new string('x', 301) })
            Assert.Equal(400, Status(await AdminTaxiRequestHandlers.CancelRequest(RequestId, new AdminCancelTaxiRequestDto { Note = note }, service, null!, default)));
        Assert.Equal(400, Status(await AdminTaxiRequestHandlers.CancelRequest(RequestId, null, service, null!, default)));

        Assert.Null(TaxiRequestCreateRules.AdminNoteCheck(new string('x', 300)).Error);
        Assert.Null(TaxiRequestCreateRules.AdminNoteCheck("x").Error);
        Assert.NotNull(TaxiRequestCreateRules.AdminNoteCheck(new string('x', 301)).Error);
    }

    [Fact]
    public void Invite_code_is_in_no_admin_dto()
    {
        var types = typeof(AdminTaxiRequestListDto).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(AdminTaxiRequestListDto).Namespace && t.Name.StartsWith("Admin", StringComparison.Ordinal))
            .Append(typeof(AdminTaxiDto)).ToList();
        Assert.True(types.Count >= 10);
        foreach (var type in types)
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Assert.DoesNotContain("invite", p.Name, StringComparison.OrdinalIgnoreCase);

        foreach (var name in new[] { "admin_taxi_request_list.json", "admin_taxi_request_detail.json", "admin_taxi_bookings.json", "admin_taxi_health.json" })
            Assert.DoesNotContain("invite", Sample(name).ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    private static readonly DateTime Now = new(2026, 12, 10, 12, 0, 0, DateTimeKind.Utc);

    private static TaxiRequest Quiet(Action<TaxiRequest>? change = null)
    {
        var r = new TaxiRequest { Status = RequestStates.Open, AcceptedCount = 0, UpdatedAt = Now.AddMinutes(-16) };
        change?.Invoke(r);
        return r;
    }

    [Fact]
    public void Stuck_rule_matches_the_contract()
    {
        Assert.True(AdminTaxiRequestRules.IsStuck(Quiet(), false, Now));
        Assert.True(AdminTaxiRequestRules.IsStuck(Quiet(r => r.Status = RequestStates.Collecting), false, Now));
        Assert.False(AdminTaxiRequestRules.IsStuck(Quiet(), true, Now), "a ringing offer");
        Assert.False(AdminTaxiRequestRules.IsStuck(Quiet(r => r.AcceptedCount = 1), false, Now), "an accepted driver");
        Assert.False(AdminTaxiRequestRules.IsStuck(Quiet(r => r.UpdatedAt = Now.AddMinutes(-15).AddSeconds(1)), false, Now), "not yet 15 minutes");
        Assert.True(AdminTaxiRequestRules.IsStuck(Quiet(r => r.UpdatedAt = Now.AddMinutes(-15)), false, Now), "exactly 15 minutes");
        Assert.False(AdminTaxiRequestRules.IsStuck(Quiet(r => r.QueueWaitStartedAt = Now.AddMinutes(-5)), false, Now), "wait still running");
        Assert.True(AdminTaxiRequestRules.IsStuck(Quiet(r => r.QueueWaitStartedAt = Now.AddMinutes(-11)), false, Now), "wait over");
        foreach (var s in new[] { RequestStates.Selected, RequestStates.Booked, RequestStates.InProgress, RequestStates.Completed, RequestStates.Cancelled, RequestStates.Expired })
            Assert.False(AdminTaxiRequestRules.IsStuck(Quiet(r => r.Status = s), false, Now), s);
    }

    [Fact]
    public void Offers_answered_percent_is_null_without_offers()
        => Assert.Null(AdminTaxiRequestRules.AnsweredPercent(0, 0));

    [Theory]
    [InlineData(82, 18, 82)]
    [InlineData(1, 2, 33)]
    [InlineData(2, 1, 67)]
    [InlineData(0, 5, 0)]
    [InlineData(5, 0, 100)]
    public void Offers_answered_percent_counts_accepts_and_declines(int answered, int unanswered, int expected)
        => Assert.Equal(expected, AdminTaxiRequestRules.AnsweredPercent(answered, unanswered));

    [Fact]
    public void Cursor_round_trips_and_rejects_garbage()
    {
        var id = Guid.NewGuid();
        Assert.True(AdminTaxiRequestRules.TryDecodeCursor(AdminTaxiRequestRules.EncodeCursor(Created, id), out var at, out var back));
        Assert.Equal((Created, id), (at, back));
        foreach (var bad in new[] { null, "", "x", "1_2", "-5_" + id.ToString("N"), "99999999999999999999_" + id.ToString("N") })
            Assert.False(AdminTaxiRequestRules.TryDecodeCursor(bad, out _, out _), bad);
    }

    [Fact]
    public void State_filters_and_page_size()
    {
        Assert.Equal(RequestStates.Active, AdminTaxiRequestRules.RequestStatesOf(null));
        Assert.Equal(RequestStates.Final, AdminTaxiRequestRules.RequestStatesOf("Ended"));
        Assert.Equal(RequestStates.All, AdminTaxiRequestRules.RequestStatesOf("all"));
        Assert.Null(AdminTaxiRequestRules.RequestStatesOf("x"));
        Assert.Equal(BookingStates.Active, AdminTaxiRequestRules.BookingStatesOf("active"));
        Assert.Null(AdminTaxiRequestRules.BookingStatesOf("x"));
        Assert.Equal(20, AdminTaxiRequestRules.PageSize(null));
        Assert.Equal(1, AdminTaxiRequestRules.PageSize(0));
        Assert.Equal(50, AdminTaxiRequestRules.PageSize(999));
    }

    [Fact]
    public void Ack_percentile_rating_and_ring_ready()
    {
        Assert.Null(AdminTaxiRequestRules.Percentile95([]));
        Assert.Equal(2.4, AdminTaxiRequestRules.Percentile95([2.4]));
        Assert.Equal(19.0, AdminTaxiRequestRules.Percentile95(Enumerable.Range(1, 20).Select(x => (double)x).ToList()));
        Assert.Null(AdminTaxiRequestRules.RatingAverage(0, 0));
        Assert.Equal(4.8, AdminTaxiRequestRules.RatingAverage(346, 72));

        var l = new TaxiListing { DrivingLicenceNumberEnc = "x", DateOfBirth = new DateOnly(1990, 1, 1), RingReadyAt = Now, RingNotificationsOn = true, RingMissStreak = 2 };
        Assert.True(AdminTaxiRequestRules.RingReady(l, true));
        Assert.False(AdminTaxiRequestRules.RingReady(l, false));
        l.RingMissStreak = 3;
        Assert.False(AdminTaxiRequestRules.RingReady(l, true));
        l.RingMissStreak = 0;
        l.RingNotificationsOn = false;
        Assert.False(AdminTaxiRequestRules.RingReady(l, true));
        l.RingNotificationsOn = true;
        l.RingReadyAt = null;
        Assert.False(AdminTaxiRequestRules.RingReady(l, true));
    }

    [Fact]
    public void Phone_search_digits()
    {
        Assert.Equal("9876", AdminTaxiRequestRules.PhoneDigits("+9876"));
        Assert.Equal("", AdminTaxiRequestRules.PhoneDigits("+"));
        Assert.Equal("", AdminTaxiRequestRules.PhoneDigits("++"));
        Assert.Null(AdminTaxiRequestRules.PhoneDigits("Aarav"));
        Assert.Null(AdminTaxiRequestRules.PhoneDigits("98 76"));
    }

    [Fact]
    public void Stuck_expression_and_compiled_rule_agree()
    {
        var rows = new[]
        {
            Quiet(), Quiet(r => r.AcceptedCount = 1), Quiet(r => r.UpdatedAt = Now.AddMinutes(-14)),
            Quiet(r => r.QueueWaitStartedAt = Now.AddMinutes(-5)), Quiet(r => r.QueueWaitStartedAt = Now.AddMinutes(-11)),
            Quiet(r => r.Status = RequestStates.Booked),
        };
        var byExpression = rows.AsQueryable().Where(AdminTaxiRequestRules.StuckRequestPart(Now)).ToList();
        Assert.Equal(rows.Where(r => AdminTaxiRequestRules.IsStuck(r, false, Now)).ToList(), byExpression);
        Assert.Equal(2, byExpression.Count);
    }

    // Seven rows, three of them created in the same tick: every page size walks them all once, in order.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(50)]
    public void Keyset_pages_have_no_duplicate_and_no_gap(int take)
    {
        var tie = Created.AddMinutes(-5);
        var rows = new[] { Created, tie, tie, tie, Created.AddMinutes(-9), Created.AddMinutes(-1), Created.AddMinutes(-30) }
            .Select(at => new TaxiRequest { Id = Guid.NewGuid(), CreatedAt = at, UpdatedAt = at, Status = RequestStates.Open }).ToList();
        var expected = rows.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Select(r => r.Id).ToList();

        var seen = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 20; guard++)
        {
            (DateTime, Guid)? after = null;
            if (cursor is not null)
            {
                Assert.True(AdminTaxiRequestRules.TryDecodeCursor(cursor, out var at, out var id));
                after = (at, id);
            }
            var page = AdminTaxiRequestQueries.RequestPage(rows.AsQueryable(), after, take).ToList();
            var more = page.Count > take;
            page = page.Take(take).ToList();
            seen.AddRange(page.Select(r => r.Id));
            if (!more) break;
            cursor = AdminTaxiRequestRules.EncodeCursor(page[^1].CreatedAt, page[^1].Id);
        }
        Assert.Equal(expected, seen);

        var bookings = rows.Select(r => new TaxiBooking { Id = r.Id, BookedAt = r.CreatedAt, Status = BookingStates.Booked }).ToList();
        var firstTwo = AdminTaxiRequestQueries.BookingPage(bookings.AsQueryable(), null, 2).Take(2).ToList();
        var rest = AdminTaxiRequestQueries.BookingPage(bookings.AsQueryable(), (firstTwo[^1].BookedAt, firstTwo[^1].Id), 50).ToList();
        Assert.Equal(expected, firstTwo.Concat(rest).Select(b => b.Id).ToList());
    }

    private static ApplicationDbContext DesignContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite()).Options);

    // Translation to PostgreSQL SQL, no connection: a query EF cannot translate throws here.
    [Fact]
    public void Every_admin_query_translates_to_sql()
    {
        using var db = DesignContext();
        var states = RequestStates.Active;
        var at = (Created, RequestId);

        var page = AdminTaxiRequestQueries.RequestPage(AdminTaxiRequestQueries.Requests(db, states, 210000050, null), at, 20).ToQueryString();
        Assert.Matches("\"CreatedAt\" < @\\w+ OR \\(\\w+\\.\"CreatedAt\" = @\\w+ AND \\w+\\.\"Id\" > @\\w+\\)", page);
        Assert.Matches("ORDER BY \\w+\\.\"CreatedAt\" DESC, \\w+\\.\"Id\"\\s+LIMIT @\\w+", page);
        Assert.Contains("\"DispatchCityKey\" = @", page);

        var byName = AdminTaxiRequestQueries.Requests(db, states, null, "Aarav").ToQueryString();
        Assert.Matches("EXISTS \\(\\s+SELECT 1\\s+FROM \"Users\"", byName);
        Assert.Contains("lower(", byName);
        var byPhone = AdminTaxiRequestQueries.Requests(db, states, null, "+9876").ToQueryString();
        Assert.Contains("\"PhoneNumber\" LIKE", byPhone);
        Assert.DoesNotContain("\"Users\"", AdminTaxiRequestQueries.Requests(db, states, null, "+").ToQueryString());

        var bookings = AdminTaxiRequestQueries.BookingPage(AdminTaxiRequestQueries.Bookings(db, BookingStates.Active), at, 20).ToQueryString();
        Assert.Matches("\"BookedAt\" = @\\w+ AND \\w+\\.\"Id\" > @\\w+", bookings);
        Assert.Matches("ORDER BY \\w+\\.\"BookedAt\" DESC, \\w+\\.\"Id\"", bookings);

        var contacts = AdminTaxiRequestQueries.DriverContacts(db, [Guid.NewGuid()]).ToQueryString();
        Assert.Contains("JOIN \"Users\"", contacts);

        var stuck = AdminTaxiRequestQueries.Stuck(db, Now).ToQueryString();
        Assert.Matches(@"NOT \(?EXISTS", stuck);
        Assert.Contains("\"QueueWaitStartedAt\" IS NULL", stuck);
        Assert.Matches(@"""QueueWaitStartedAt"" <= @\w*WaitOver", stuck);
        Assert.Matches(@"""UpdatedAt"" <= @\w*Quiet", stuck);

        foreach (var summary in new[] { AdminTaxiRequestQueries.PendingDueActions(db).Take(1).ToQueryString(), AdminTaxiRequestQueries.PendingOutbox(db).Take(1).ToQueryString() })
        {
            Assert.Contains("count(*)", summary);
            Assert.Contains("min(", summary);
        }

        var ready = AdminTaxiRequestQueries.RingReadyDrivers(db).ToQueryString();
        Assert.Contains("\"DeviceTokens\"", ready);
        Assert.Contains("\"RingReadyAt\" IS NOT NULL", ready);
        Assert.Contains("\"RingMissStreak\" >=", AdminTaxiRequestQueries.DriversMissingRings(db).ToQueryString());

        var unanswered = AdminTaxiRequestQueries.UnansweredOffers(db, Now).ToQueryString();
        Assert.Contains("\"RespondedAt\" IS NULL", unanswered);
        Assert.DoesNotContain("\"EndReason\" =", unanswered);
        Assert.Contains("\"RespondedAt\" IS NOT NULL", AdminTaxiRequestQueries.AnsweredOffers(db, Now).ToQueryString());
        Assert.Contains("\"DriverListingId\" = @", AdminTaxiRequestQueries.DriverMissedOffers(db, Guid.NewGuid()).ToQueryString());
        Assert.Contains("\"DriverListingId\" = @", AdminTaxiRequestQueries.DriverAnsweredOffers(db, Guid.NewGuid()).ToQueryString());
    }

    // Contract 11: every offer that left Ringing without an answer counts against the percent, whatever closed it.
    [Fact]
    public void Unanswered_offers_are_every_offer_that_stopped_ringing_unanswered()
    {
        var since = Now.AddDays(-7);
        TaxiRequestOffer O(string status, string? reason, DateTime? closed, DateTime? responded = null)
            => new() { Id = Guid.NewGuid(), Status = status, EndReason = reason, ClosedAt = closed, RespondedAt = responded };
        var offers = new[]
        {
            O(OfferStates.Missed, OfferEndReasons.Expired, Now.AddHours(-1)),
            O(OfferStates.Closed, null, Now.AddHours(-2)),
            O(OfferStates.Ringing, null, null),
            O(OfferStates.Missed, OfferEndReasons.Expired, Now.AddDays(-8)),
            O(OfferStates.Declined, null, Now.AddHours(-3), Now.AddHours(-3)),
        };
        Assert.Equal(offers[..2], offers.AsQueryable().Where(AdminTaxiRequestRules.StoppedRingingUnanswered(since)).ToArray());
    }
}
