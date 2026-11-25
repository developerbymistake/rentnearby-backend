using RentNearBy.Api.Handlers;

namespace RentNearBy.Tests.Trips;

public class TripOwnerShareTests
{
    private static readonly Guid Traveller = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 11, 25, 6, 0, 0, DateTimeKind.Utc);

    private static TripEnquiry Booking(string status = TripEnquiryStatuses.ConfirmedByBoth) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Stay,
        TravellerUserId = Traveller,
        OwnerUserId = Owner,
        Status = status,
    };

    private const string Card = """{"stayName":"Deodar","upiId":"deodar@upi","whatsApp":"+919800000003","text":"x"}""";

    [Fact]
    public void Card_found_gives_its_values_and_time()
    {
        var share = TripEnquiryDtoBuilder.OwnerShare(Booking(), Traveller, (Card, At), null, null);
        Assert.NotNull(share);
        Assert.Equal("deodar@upi", share!.UpiId);
        Assert.Equal("+919800000003", share.WhatsApp);
        Assert.Equal(At, share.SharedAt);
    }

    [Fact]
    public void Mark_only_reads_the_live_values()
    {
        var share = TripEnquiryDtoBuilder.OwnerShare(Booking(), Traveller, null, At, ("live@upi", null));
        Assert.Equal("live@upi", share!.UpiId);
        Assert.Null(share.WhatsApp);
        Assert.Equal(At, share.SharedAt);
    }

    [Fact]
    public void Nothing_shared_is_null() => Assert.Null(TripEnquiryDtoBuilder.OwnerShare(Booking(), Traveller, null, null, ("live@upi", null)));

    [Fact]
    public void Owner_view_and_unconfirmed_rows_never_get_it()
    {
        Assert.Null(TripEnquiryDtoBuilder.OwnerShare(Booking(), Owner, (Card, At), At, ("live@upi", null)));
        Assert.Null(TripEnquiryDtoBuilder.OwnerShare(Booking(TripEnquiryStatuses.Offered), Traveller, (Card, At), At, ("live@upi", null)));
    }

    [Fact]
    public void Mark_key_is_the_contract_key()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"share:{id}", TripEnquiryDtoBuilder.ShareMarkKey(id));
    }
}
