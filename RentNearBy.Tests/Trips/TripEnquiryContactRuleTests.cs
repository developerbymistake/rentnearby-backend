using System.Text.Json;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Trips;

public class TripEnquiryContactRuleTests
{
    private static readonly Guid Traveller = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 25, 6, 0, 0, DateTimeKind.Utc);

    private static TripEnquiry Row(string status, bool? phoneShared) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Stay,
        TravellerUserId = Traveller,
        OwnerUserId = Owner,
        ListingId = Guid.NewGuid(),
        ListingName = "Deodar Homestay",
        StepKey = "stay:1",
        CheckIn = new DateOnly(2026, 12, 1),
        CheckOut = new DateOnly(2026, 12, 3),
        Status = status,
        CreatedAt = Now.AddHours(-3),
        UpdatedAt = Now.AddHours(-3),
        ExpiresAt = Now.AddHours(69),
        ContextJson = JsonSerializer.Serialize(new TripEnquiryContext { City = "Nainital", PhoneShared = phoneShared }, TripJson.Options),
    };

    private static TripEnquiryDto Build(TripEnquiry e, Guid viewer) => TripEnquiryDtoBuilder.Build(
        e, [], new TripPartyInfo(Traveller, "Rahul", "+919800000001", false, "+919800000001"),
        new TripPartyInfo(Owner, "Mohan", "+919800000002", true, "+919800000003"), viewer, Now);

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked, true, true)]
    [InlineData(TripEnquiryStatuses.Offered, true, true)]
    [InlineData(TripEnquiryStatuses.Countered, true, true)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, true, true)]
    [InlineData(TripEnquiryStatuses.Asked, null, false)]
    [InlineData(TripEnquiryStatuses.Asked, false, false)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, null, true)]
    [InlineData(TripEnquiryStatuses.Expired, true, false)]
    [InlineData(TripEnquiryStatuses.NotAvailable, true, false)]
    [InlineData(TripEnquiryStatuses.Closed, true, false)]
    public void Host_sees_the_shared_phone_only_while_the_ask_is_open(string status, bool? shared, bool visible)
    {
        var dto = Build(Row(status, shared), Owner);
        Assert.Equal(visible ? "+919800000001" : null, dto.Traveller.Phone);
    }

    [Fact]
    public void The_traveller_never_gets_her_own_number_back_and_no_whatsapp_goes_to_the_host()
    {
        var e = Row(TripEnquiryStatuses.Asked, true);
        Assert.Null(Build(e, Traveller).Traveller.Phone);
        Assert.Null(Build(e, Owner).Owner.WhatsApp);
    }

    [Fact]
    public void Owner_phone_rule_is_unchanged()
    {
        var dto = Build(Row(TripEnquiryStatuses.Asked, true), Traveller);
        Assert.Equal("+919800000002", dto.Owner.Phone);
    }

    [Fact]
    public void Host_view_carries_waiting_hours_and_offers_left_traveller_view_does_not()
    {
        var e = Row(TripEnquiryStatuses.Asked, true);
        var host = Build(e, Owner);
        Assert.Equal(3, host.WaitingHours);
        Assert.Equal(TripExecutionRules.MaxOffersPerEnquiry, host.OffersLeft);
        Assert.Equal("stay:1", host.StepKey);
        Assert.Null(host.QuietAt);
        var traveller = Build(e, Traveller);
        Assert.Null(traveller.WaitingHours);
        Assert.Null(traveller.OffersLeft);
        Assert.NotNull(traveller.QuietAt);
        Assert.Null(traveller.EndReason);
    }

    [Fact]
    public void Expired_offers_do_not_count_toward_offers_left()
    {
        var e = Row(TripEnquiryStatuses.Asked, true);
        TripOffer Offer(string status) => new() { Id = Guid.NewGuid(), TripEnquiryId = e.Id, Kind = TripOfferKinds.Offer, Status = status, CreatedAt = Now };
        var dto = TripEnquiryDtoBuilder.Build(e, [Offer(TripOfferStatuses.Expired), Offer(TripOfferStatuses.Expired)],
            new TripPartyInfo(Traveller, "Rahul", null, false, null), new TripPartyInfo(Owner, "Mohan", null, true, null), Owner, Now);
        Assert.Equal(TripExecutionRules.MaxOffersPerEnquiry, dto.OffersLeft);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(59, 0)]
    [InlineData(60, 1)]
    [InlineData(150, 2)]
    public void Waiting_hours_are_whole_hours(int minutes, int hours)
        => Assert.Equal(hours, TripEnquiryDtoBuilder.WaitingHoursOf(Now.AddMinutes(-minutes), Now));
}
