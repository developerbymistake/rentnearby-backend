using System.Text.Json;
using System.Text.Json.Nodes;
using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// The traveller object built from in-memory rows equals each contract sample, node for node.
public class TaxiRequestViewsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Guid RequestId = Guid.Parse("3f9a6c2d-1b7e-4f0a-8c55-2e7d9b4a6101");
    private static readonly Guid TripId = Guid.Parse("7b1c2f0e-5a44-4c1e-9d3a-0f6f2a1b9c01");
    private static readonly Guid BookingId = Guid.Parse("b00c1e55-0001-4b00-8000-000000000001");
    private static readonly DateTime Created = Utc("2026-12-10T09:41:07Z");

    private static DateTime Utc(string iso) => DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    private static Guid OfferId(int n) => Guid.Parse($"a1b2c3d4-000{n}-4a00-8000-00000000000{n}");

    private static TaxiRequest Request(string state, long version, string updated, short accepted = 0, string? endReason = null) => new()
    {
        Id = RequestId,
        TripId = TripId,
        TravellerUserId = Guid.NewGuid(),
        Status = state,
        EndReason = endReason,
        WantPickup = true,
        WantSightseeing = true,
        WantDrop = true,
        PickupCityKey = 6377270,
        PickupCityName = "Kathgodam",
        DropCityKey = 6377270,
        DropCityName = "Kathgodam",
        DropSameAsPickup = true,
        Seats = 4,
        Travellers = 2,
        DateFrom = new DateOnly(2026, 12, 18),
        DateTo = new DateOnly(2026, 12, 21),
        SightseeingDaysJson = "[1,2,4]",
        AcceptedCount = accepted,
        Version = version,
        CreatedAt = Created,
        UpdatedAt = Utc(updated),
    };

    private static TaxiViewOffer Offer(int n, string status, int seq, DateTime? confirmBy = null) => new(
        OfferId(n), status, seq, confirmBy,
        n == 1 ? "Ramesh Kumar" : "Mohan Singh",
        n == 1 ? "+919876500001" : "+919876500002",
        n == 1 ? "Swift Dzire" : "Honda Amaze",
        4,
        n == 1 ? 346 : 235,
        n == 1 ? 72 : 51,
        n == 1 ? 120 : 86);

    private static TaxiViewOffer Plain(int seq, string status) => new(Guid.NewGuid(), status, seq, null, "Other", "+910000000000", "Car", 4, 0, 0, 0);

    private static TaxiViewBooking Booking(string status) => new(BookingId, status, new DateOnly(2026, 12, 18), new DateOnly(2026, 12, 21));

    private static TaxiViewInput Input(TaxiRequest r, IReadOnlyList<TaxiViewOffer> offers, TaxiViewBooking? booking = null, TaxiViewRating? rating = null,
        DateOnly? today = null)
        => new(r, "Uttarakhand Hills Trip", 4, offers.Count, offers,
            offers.Any(o => o.Status is OfferStates.Missed or OfferStates.Declined), booking, rating, today ?? new DateOnly(2026, 12, 10));

    public static IEnumerable<object[]> Cases()
    {
        yield return Case("taxi_request_finding.json", () => Input(Request(RequestStates.Open, 2, "2026-12-10T09:41:09Z"),
            [Plain(1, OfferStates.Ringing), Plain(2, OfferStates.Ringing), Plain(3, OfferStates.Ringing)]));

        yield return Case("taxi_request_waiting.json", () =>
        {
            var r = Request(RequestStates.Open, 3, "2026-12-10T09:41:08Z");
            r.QueueWaitStartedAt = Created;
            return Input(r, []);
        });

        yield return Case("taxi_request_no_reply.json", () => Input(Request(RequestStates.Open, 5, "2026-12-10T09:46:10Z"),
            [Plain(1, OfferStates.Missed), Plain(2, OfferStates.Missed), Plain(3, OfferStates.Missed), Plain(4, OfferStates.Ringing), Plain(5, OfferStates.Ringing)]));

        yield return Case("taxi_request_collecting.json", () => Input(Request(RequestStates.Collecting, 6, "2026-12-10T09:44:30Z", accepted: 2),
            [Offer(1, OfferStates.Accepted, 1), Offer(2, OfferStates.Accepted, 2), Plain(3, OfferStates.Ringing)]));

        yield return Case("taxi_request_selected.json", () => Input(Request(RequestStates.Selected, 8, "2026-12-10T09:46:00Z", accepted: 2),
            [Offer(1, OfferStates.Selected, 1, Utc("2026-12-10T10:01:00Z")), Offer(2, OfferStates.Accepted, 2), Plain(3, OfferStates.Missed)]));

        yield return Case("taxi_request_booked.json", () => Input(Request(RequestStates.Booked, 9, "2026-12-10T09:52:12Z", accepted: 2),
            [Offer(1, OfferStates.Confirmed, 1), Plain(2, OfferStates.NotSelected), Plain(3, OfferStates.NotSelected)], Booking(BookingStates.Booked)));

        yield return Case("taxi_request_in_progress.json", () => Input(Request(RequestStates.InProgress, 10, "2026-12-17T18:30:00Z", accepted: 2),
            [Offer(1, OfferStates.Confirmed, 1), Plain(2, OfferStates.NotSelected), Plain(3, OfferStates.NotSelected)], Booking(BookingStates.InProgress),
            today: new DateOnly(2026, 12, 20)));

        yield return Case("taxi_request_rate.json", () => Input(Request(RequestStates.Completed, 11, "2026-12-21T18:35:00Z", accepted: 2),
            [Offer(1, OfferStates.Confirmed, 1, null) with { RidesCompleted = 121 }, Plain(2, OfferStates.NotSelected), Plain(3, OfferStates.NotSelected)],
            Booking(BookingStates.Completed)));

        yield return Case("taxi_request_busy.json", () => Input(Request(RequestStates.Expired, 7, "2026-12-10T10:02:00Z", endReason: RequestEndReasons.AllDriversBusy),
            Enumerable.Range(1, 6).Select(i => Plain(i, OfferStates.Missed)).ToList()));
    }

    private static object[] Case(string sample, Func<TaxiViewInput> input) => [sample, input];

    [Theory]
    [MemberData(nameof(Cases))]
    public void View_dto_equals_each_contract_sample(string sample, Func<TaxiViewInput> input)
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, sample)));
        var written = JsonNode.Parse(JsonSerializer.Serialize(TaxiRequestViews.Build(input()), Web));
        WithoutAdditiveFields(written!);
        Assert.True(JsonNode.DeepEquals(expected, written), $"{sample}\nexpected {expected}\nwas      {written}");
    }

    // Fields added after contract version 1 (CONTRACT_DELTA.md); the shared samples stay at version 1, old apps ignore them.
    private static readonly string[] AdditiveBookingFields = ["canRate", "leftDuringTrip"];

    private static void WithoutAdditiveFields(JsonNode written)
    {
        if (written["booking"] is not JsonObject booking) return;
        foreach (var field in AdditiveBookingFields)
            Assert.True(booking.Remove(field), $"booking.{field} missing");
    }

    // O13: rating is offered after a completed trip and after a trip that was cancelled while it was running.
    [Theory]
    [InlineData(RequestStates.Completed, null, BookingStates.Completed, false, false, true, false)]
    [InlineData(RequestStates.Completed, null, BookingStates.Completed, false, true, false, false)]
    [InlineData(RequestStates.Cancelled, RequestEndReasons.DriverLeftTrip, BookingStates.Cancelled, true, false, true, true)]
    [InlineData(RequestStates.Cancelled, RequestEndReasons.AdminCancelled, BookingStates.Cancelled, true, false, true, true)]
    [InlineData(RequestStates.Cancelled, RequestEndReasons.DriverLeftTrip, BookingStates.Cancelled, true, true, false, true)]
    [InlineData(RequestStates.Cancelled, RequestEndReasons.AdminCancelled, BookingStates.Cancelled, false, false, false, false)]
    [InlineData(RequestStates.Cancelled, RequestEndReasons.AccountClosed, BookingStates.Cancelled, true, false, false, false)]
    [InlineData(RequestStates.Cancelled, RequestEndReasons.TravellerCancelled, BookingStates.Cancelled, false, false, false, false)]
    [InlineData(RequestStates.InProgress, null, BookingStates.InProgress, true, false, false, false)]
    [InlineData(RequestStates.Booked, null, BookingStates.Booked, false, false, false, false)]
    public void Booking_says_whether_the_traveller_can_rate(string state, string? endReason, string bookingState, bool started, bool rated,
        bool canRate, bool leftDuringTrip)
    {
        var booking = Booking(bookingState) with { StartedAt = started ? Utc("2026-12-17T18:30:00Z") : null };
        var rating = rated ? new TaxiViewRating(3, Utc("2026-12-19T04:10:00Z")) : null;
        var dto = TaxiRequestViews.Build(Input(Request(state, 12, "2026-12-19T04:10:00Z", accepted: 1, endReason: endReason),
            [Offer(1, OfferStates.Confirmed, 1)], booking, rating));
        Assert.Equal(((bool?)canRate, (bool?)leftDuringTrip), (dto.Booking!.CanRate, dto.Booking.LeftDuringTrip));
    }

    [Fact]
    public void Every_state_sample_is_covered()
        => Assert.Equal(9, Cases().Count());

    [Theory]
    [InlineData(BookingStates.Completed)]
    [InlineData(BookingStates.Cancelled)]
    public void Phone_is_hidden_after_the_booking_ends(string bookingState)
    {
        var dto = TaxiRequestViews.Build(Input(Request(RequestStates.Completed, 11, "2026-12-21T18:35:00Z", accepted: 1),
            [Offer(1, OfferStates.Confirmed, 1)], Booking(bookingState)));
        Assert.Null(Assert.Single(dto.Drivers).Phone);
    }

    [Theory]
    [InlineData(BookingStates.Booked)]
    [InlineData(BookingStates.InProgress)]
    public void Phone_is_shown_while_the_booking_is_live(string bookingState)
    {
        var dto = TaxiRequestViews.Build(Input(Request(RequestStates.Booked, 9, "2026-12-10T09:52:12Z", accepted: 1),
            [Offer(1, OfferStates.Confirmed, 1)], Booking(bookingState)));
        Assert.Equal("+919876500001", Assert.Single(dto.Drivers).Phone);
    }

    [Fact]
    public void Not_agreed_driver_is_not_listed()
    {
        var dto = TaxiRequestViews.Build(Input(Request(RequestStates.Open, 4, "2026-12-10T09:45:00Z", accepted: 1),
            [Offer(1, OfferStates.NotAgreed, 1), Offer(2, OfferStates.Declined, 2), Plain(3, OfferStates.Missed), Plain(4, OfferStates.Closed)]));
        Assert.Empty(dto.Drivers);
        Assert.Equal(RequestViews.NoReply, dto.View);
    }

    [Fact]
    public void Rated_request_shows_done_and_the_rating()
    {
        var rated = new TaxiViewRating(5, Utc("2026-12-22T04:10:00Z"));
        var dto = TaxiRequestViews.Build(Input(Request(RequestStates.Completed, 12, "2026-12-22T04:10:00Z", accepted: 1),
            [Offer(1, OfferStates.Confirmed, 1)], Booking(BookingStates.Completed), rated));
        Assert.Equal(RequestViews.Done, dto.View);
        Assert.Equal(5, dto.Rating!.Stars);
    }

    [Fact]
    public void Rating_average_is_null_without_ratings()
    {
        var dto = TaxiRequestViews.Build(Input(Request(RequestStates.Collecting, 6, "2026-12-10T09:44:30Z", accepted: 1),
            [Plain(1, OfferStates.Accepted)]));
        var driver = Assert.Single(dto.Drivers);
        Assert.Null(driver.RatingAverage);
        Assert.Equal(0, driver.RatingCount);
    }

    [Fact]
    public void Sections_off_leave_pickup_and_drop_null()
    {
        var r = Request(RequestStates.Open, 2, "2026-12-10T09:41:09Z");
        r.WantPickup = false;
        r.WantDrop = false;
        r.PickupCityKey = null;
        r.DropCityKey = null;
        var dto = TaxiRequestViews.Build(Input(r, []));
        Assert.Null(dto.Pickup);
        Assert.Null(dto.Drop);
        Assert.True(dto.Sections.Sightseeing);
    }
}
