using Microsoft.AspNetCore.Http;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Entities;

namespace RentNearBy.Tests.Trips;

// Bulk enquiry rules: caps of 3, results cover every requested id once, replay and idempotency key.
public class TripBulkEnquiryRulesTests
{
    private static readonly CreateTripEnquiryRequestValidator Validator = new();

    private static Guid[] Ids(int n) => Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToArray();

    private static CreateTripEnquiryRequest StayRequest(params Guid[] ids) => new() { Kind = TripEnquiryKinds.Stay, Day = 1, ListingIds = ids.ToList() };

    private static TripEnquiry Row(Guid listingId, Guid? requestId = null) => new() { Id = Guid.NewGuid(), ListingId = listingId, ClientRequestId = requestId };

    [Fact]
    public void Every_cap_is_three_and_the_window_is_72_hours()
    {
        Assert.Equal(3, TripExecutionRules.MaxRecipientsPerEnquiry);
        Assert.Equal(3, TripExecutionRules.MaxShortlistPerStep);
        Assert.Equal(3, TripExecutionRules.MaxOpenPerNight);
        Assert.Equal(3, TripExecutionRules.MaxOffersPerEnquiry);
        Assert.Equal(3, JourneyRules.MaxAskPerLeg);
        Assert.Equal(3, JourneyRules.MaxAskTaxis);
        Assert.Equal(72, TripExecutionRules.EnquiryHours);
        Assert.Equal(24, TripExecutionRules.OfferHours);
        Assert.Equal(2, TripExecutionRules.OfferUrgentHours);
    }

    [Fact]
    public void One_to_three_recipients_pass_and_four_fail_with_the_pick_message()
    {
        foreach (var n in new[] { 1, 2, 3 }) Assert.True(Validator.Validate(StayRequest(Ids(n))).IsValid);
        var four = Validator.Validate(StayRequest(Ids(4)));
        Assert.Equal(TripExecutionRules.PickMessage, four.Errors[0].ErrorMessage);
        Assert.Equal("Pick 1 to 3.", TripExecutionRules.PickMessage);
        Assert.False(Validator.Validate(StayRequest()).IsValid);
    }

    [Fact]
    public void Duplicates_are_removed_before_the_cap_is_counted()
    {
        var ids = Ids(3);
        Assert.True(Validator.Validate(StayRequest(ids[0], ids[0], ids[1], ids[2])).IsValid);
    }

    [Fact]
    public void Step_key_and_request_id_are_checked()
    {
        var request = StayRequest(Ids(1));
        request.StepKey = "stay:1";
        request.ClientRequestId = Guid.NewGuid();
        Assert.True(Validator.Validate(request).IsValid);
        request.StepKey = "stay:0";
        Assert.False(Validator.Validate(request).IsValid);
        request.StepKey = null;
        request.ClientRequestId = Guid.Empty;
        Assert.False(Validator.Validate(request).IsValid);
    }

    [Fact]
    public void Results_cover_every_requested_id_once_in_request_order()
    {
        var ids = Ids(3);
        var sent = Row(ids[0]);
        var open = Row(ids[1]);
        var results = TripBulk.BuildResults(ids, [sent.Id], [], [(ids[2], TripSkipReasons.NotAvailable)], [sent, open], null);

        Assert.Equal(ids, results.Select(r => r.ListingId));
        Assert.Equal(TripSendStatuses.Sent, results[0].Status);
        Assert.Equal(sent.Id, results[0].EnquiryId);
        Assert.Equal(TripSendStatuses.AlreadyOpen, results[1].Status);
        Assert.Equal(open.Id, results[1].EnquiryId);
        Assert.Equal(TripSendStatuses.NotAvailable, results[2].Status);
        Assert.Null(results[2].EnquiryId);
    }

    [Theory]
    [InlineData(TripSkipReasons.AlreadyOpen, TripSendStatuses.AlreadyOpen)]
    [InlineData(TripSkipReasons.AlreadyBooked, TripSendStatuses.AlreadyOpen)]
    [InlineData(TripSkipReasons.NotAvailable, TripSendStatuses.NotAvailable)]
    [InlineData(TripSkipReasons.Blocked, TripSendStatuses.Blocked)]
    [InlineData(TripSkipReasons.LimitReached, TripSendStatuses.LimitReached)]
    public void Skip_reasons_map_to_statuses(string reason, string status)
    {
        var id = Guid.NewGuid();
        var result = Assert.Single(TripBulk.BuildResults([id], [], [], [(id, reason)], [], null));
        Assert.Equal(status, result.Status);
    }

    [Fact]
    public void A_listing_nothing_answers_for_is_reported_failed_never_dropped()
    {
        var id = Guid.NewGuid();
        var result = Assert.Single(TripBulk.BuildResults([id], [], [], [], [], null));
        Assert.Equal(TripSendStatuses.Failed, result.Status);
    }

    [Fact]
    public void Same_request_key_replays_and_a_new_key_is_already_open()
    {
        var key = Guid.NewGuid();
        var id = Guid.NewGuid();
        var row = Row(id, key);
        Assert.Equal(TripSendStatuses.Replayed, TripBulk.BuildResults([id], [], [], [], [row], key)[0].Status);
        Assert.Equal(TripSendStatuses.AlreadyOpen, TripBulk.BuildResults([id], [], [], [], [row], Guid.NewGuid())[0].Status);
        Assert.Equal(TripSendStatuses.AlreadyOpen, TripBulk.BuildResults([id], [], [], [], [row], null)[0].Status);
    }

    [Fact]
    public void A_widened_open_row_counts_as_sent()
    {
        var id = Guid.NewGuid();
        var row = Row(id);
        Assert.Equal(TripSendStatuses.Sent, TripBulk.BuildResults([id], [], [row.Id], [], [row], null)[0].Status);
    }

    [Fact]
    public void Idempotency_key_header_is_the_request_id_and_the_body_wins()
    {
        var header = Guid.NewGuid();
        var http = new DefaultHttpContext();
        http.Request.Headers[TripBulk.IdempotencyHeader] = header.ToString();
        Assert.Equal(header, TripBulk.RequestId(null, http));
        var body = Guid.NewGuid();
        Assert.Equal(body, TripBulk.RequestId(body, http));
        Assert.Null(TripBulk.RequestId(null, new DefaultHttpContext()));
    }

    [Fact]
    public void Taxi_ids_of_an_ask_are_capped_at_three_per_car_and_three_in_all()
    {
        var ask = new AskDriversRequestValidator();
        AskDriversRequest Make(params Guid[][] legs) => new()
        {
            Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), People = 2, JourneyId = Guid.NewGuid(), FromName = "A", ToName = "B",
            Legs = legs.Select((ids, i) => new AskLegRequest
            {
                LegIndex = i + 1, LegKey = "k:1:2", FromName = "A", ToName = "B", TaxiIds = ids.ToList(),
            }).ToList(),
        };
        Assert.True(ask.Validate(Make(Ids(3))).IsValid);
        Assert.False(ask.Validate(Make(Ids(4))).IsValid);
        Assert.False(ask.Validate(Make(Ids(2), Ids(2))).IsValid);
    }
}
