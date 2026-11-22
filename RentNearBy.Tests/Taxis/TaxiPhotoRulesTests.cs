using RentNearBy.Core.Utils;

namespace RentNearBy.Tests.Taxis;

// CONTRACT_BACKEND (card photos) R2, R3, section 3: proof lock rule, display photo never gates step 1, list thumb.
public class TaxiPhotoRulesTests
{
    private static TaxiListing Taxi(string status, bool everApproved, string? plate = "https://res.cloudinary.com/x/image/upload/p.jpg",
        string? display = null) => new()
    {
        Id = Guid.NewGuid(), UserId = Guid.NewGuid(), DrivingLicenceNumberEnc = "x", DateOfBirth = new DateOnly(1985, 1, 1),
        Status = status, EverApproved = everApproved, PlatePhotoUrl = plate, DisplayPhotoUrl = display,
        VehicleTypeId = Guid.NewGuid(), Seats = 6, NumberPlate = "UK07TA1234", ContactPhone = "9000000001", WhatsAppNumber = "9000000001",
        Latitude = 29.39m, Longitude = 79.45m, DistrictId = Guid.NewGuid(),
    };

    [Theory]
    [InlineData(TaxiStatuses.Draft)]
    [InlineData(TaxiStatuses.PendingReview)]
    [InlineData(TaxiStatuses.Rejected)]
    public void ProofLocked_false_for_draft_pending_rejected_never_approved(string status)
        => Assert.False(TaxiPhotoRules.ProofLocked(Taxi(status, everApproved: false)));

    [Theory]
    [InlineData(TaxiStatuses.Draft)]
    [InlineData(TaxiStatuses.PendingReview)]
    [InlineData(TaxiStatuses.Approved)]
    [InlineData(TaxiStatuses.Rejected)]
    public void ProofLocked_true_when_ever_approved_any_status(string status)
        => Assert.True(TaxiPhotoRules.ProofLocked(Taxi(status, everApproved: true)));

    [Fact]
    public void ProofLocked_true_for_approved_without_flag()
        => Assert.True(TaxiPhotoRules.ProofLocked(Taxi(TaxiStatuses.Approved, everApproved: false)));

    [Fact]
    public void Step1Done_ignores_display_photo()
    {
        Assert.True(TaxiSetupRules.Step1Done(Taxi(TaxiStatuses.Draft, false, plate: "https://p", display: null)));
        Assert.False(TaxiSetupRules.Step1Done(Taxi(TaxiStatuses.Draft, false, plate: null, display: "https://d")));
    }

    [Fact]
    public void TaxiFace_thumb_applied_to_cloudinary_and_null_safe()
    {
        Assert.Equal("https://res.cloudinary.com/x/image/upload/c_fill,g_auto,w_320,h_320,q_auto,f_auto/bakhli/user_1/listing_2/a.jpg",
            CloudinaryThumb.Apply("https://res.cloudinary.com/x/image/upload/bakhli/user_1/listing_2/a.jpg", CloudinaryThumb.TaxiFace));
        Assert.Null(CloudinaryThumb.Apply(null, CloudinaryThumb.TaxiFace));
        Assert.Equal("https://example.com/a.jpg", CloudinaryThumb.Apply("https://example.com/a.jpg", CloudinaryThumb.TaxiFace));
        var once = CloudinaryThumb.Apply("https://res.cloudinary.com/x/image/upload/a.jpg", CloudinaryThumb.TaxiFace);
        Assert.Equal(once, CloudinaryThumb.Apply(once, CloudinaryThumb.TaxiFace));
    }
}
