using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Entities;

namespace RentNearBy.Tests.Trips;

// Shortlist rules: the clock, reopen, request validation and the DTO mapping.
public class TripShortlistRulesTests
{
    [Fact]
    public void Clock_is_the_one_enquiry_window()
    {
        var now = new DateTime(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);
        var row = new TripShortlistItem { State = TripShortlistStates.Closed, ClosedReason = TripShortlistClosedReasons.NoReply, ClosedAt = now, FinalPushedAt = now, RemindedOn = new DateOnly(2026, 11, 12) };
        TripShortlistHandlers.Reopen(row, now);
        Assert.Equal(TripShortlistStates.Active, row.State);
        Assert.Equal(now, row.AddedAt);
        Assert.Equal(now.AddHours(72), row.ClosesAt);
        Assert.Null(row.ClosedReason);
        Assert.Null(row.ClosedAt);
        Assert.Null(row.FinalPushedAt);
        Assert.Null(row.RemindedOn);
    }

    [Fact]
    public void Add_request_is_validated()
    {
        var v = new AddShortlistRequestValidator();
        Assert.True(v.Validate(new AddShortlistRequest { Kind = "Stay", Name = "Hill View" }).IsValid);
        Assert.True(v.Validate(new AddShortlistRequest { Kind = "Car", Name = "Raju", JourneyId = Guid.NewGuid(), LegIndex = 2 }).IsValid);
        Assert.False(v.Validate(new AddShortlistRequest { Kind = "Hotel", Name = "x" }).IsValid);
        Assert.False(v.Validate(new AddShortlistRequest { Kind = "Stay", Name = " " }).IsValid);
        Assert.False(v.Validate(new AddShortlistRequest { Kind = "Stay", Name = new string('a', 121) }).IsValid);
        Assert.False(v.Validate(new AddShortlistRequest { Kind = "Car", Name = "x", LegIndex = 4 }).IsValid);
    }

    [Fact]
    public void Close_accepts_only_own_or_skipped()
    {
        var v = new CloseShortlistRequestValidator();
        Assert.True(v.Validate(new CloseShortlistRequest { Reason = "Own" }).IsValid);
        Assert.True(v.Validate(new CloseShortlistRequest { Reason = "Skipped" }).IsValid);
        Assert.False(v.Validate(new CloseShortlistRequest { Reason = "NoReply" }).IsValid);
        Assert.False(v.Validate(new CloseShortlistRequest { Reason = "" }).IsValid);
    }

    [Fact]
    public void Mark_booked_listing_id_is_optional_but_never_empty()
    {
        var v = new MarkBookedRequestValidator();
        Assert.True(v.Validate(new MarkBookedRequest()).IsValid);
        Assert.True(v.Validate(new MarkBookedRequest { ListingId = Guid.NewGuid() }).IsValid);
        Assert.False(v.Validate(new MarkBookedRequest { ListingId = Guid.Empty }).IsValid);
    }

    [Fact]
    public void Dto_carries_the_contract_fields()
    {
        var now = DateTime.UtcNow;
        var item = new TripShortlistItem
        {
            StepKey = "stay:1", Kind = TripShortlistKinds.Stay, ListingId = Guid.NewGuid(), ListingName = "Hill View",
            State = TripShortlistStates.Active, AddedAt = now, ClosesAt = now.AddHours(72),
        };
        var dto = TripShortlistHandlers.ToDto(item);
        Assert.Equal("stay:1", dto.StepKey);
        Assert.Equal("Hill View", dto.Name);
        Assert.Equal(item.ListingId, dto.ListingId);
        Assert.Equal(now.AddHours(72), dto.ClosesAt);
        Assert.Null(dto.ClosedReason);
    }

    [Fact]
    public void Shortlist_error_types_and_message_are_the_contract_values()
    {
        Assert.Equal("ShortlistFull", TripEnquiryErrorTypes.ShortlistFull);
        Assert.Equal("InvalidStepKey", TripEnquiryErrorTypes.InvalidStepKey);
        Assert.Equal("TooManyRecipients", TripEnquiryErrorTypes.TooManyRecipients);
        Assert.Equal(new[] { "Own", "Skipped" }, TripShortlistClosedReasons.ClientClose);
    }
}
