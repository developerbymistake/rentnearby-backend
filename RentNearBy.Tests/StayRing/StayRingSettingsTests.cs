using RentNearBy.Api.Handlers;
using RentNearBy.Core.Models.StayRing;

namespace RentNearBy.Tests.StayRing;

// Use case 15: the readiness and switch view. A new enquiry rings only with the switch on, a live stay and a device token.
public class StayRingSettingsTests
{
    private static StayListing Stay(string status = StayStatuses.Approved, bool ringOn = true, bool deleted = false) => new()
    {
        Id = Guid.NewGuid(), UserId = Guid.NewGuid(), RegistrationTypeId = Guid.NewGuid(), RegistrationTypeName = "x", RegistrationNumber = "1",
        Status = status, EnquiryRingOn = ringOn, IsDeleted = deleted,
    };

    [Fact]
    public void Switch_defaults_on()
    {
        Assert.True(new StayListing { RegistrationTypeId = Guid.NewGuid(), RegistrationTypeName = "x", RegistrationNumber = "1" }.EnquiryRingOn);
        Assert.True(new RentNearBy.Core.DTOs.StayRing.StayRingSettingsDto().RingOn);
        Assert.True(new RentNearBy.Core.DTOs.Responses.StayOwnerDto().EnquiryRingOn);
    }

    [Theory]
    [InlineData(StayStatuses.Approved, true, false, true, true)]
    [InlineData(StayStatuses.Approved, false, false, true, false)]
    [InlineData(StayStatuses.Approved, true, false, false, false)]
    [InlineData(StayStatuses.PendingReview, true, false, true, false)]
    [InlineData(StayStatuses.Approved, true, true, true, false)]
    public void Can_ring_needs_switch_live_stay_and_token(string status, bool ringOn, bool deleted, bool token, bool expected)
    {
        var dto = StayRingHandlers.Build(Stay(status, ringOn, deleted), token);
        Assert.Equal(expected, dto.CanRing);
        Assert.Equal(ringOn, dto.RingOn);
        Assert.Equal(token, dto.HasValidToken);
        Assert.Equal(StayRingRules.RingSeconds, dto.RingSeconds);
    }

    [Fact]
    public void Rate_keys_are_per_user_and_apart_from_taxi()
    {
        var u = Guid.NewGuid();
        Assert.NotEqual(StayRingHandlers.TestRateKey(u), $"taxi:testring:{u}");
        Assert.NotEqual(StayRingHandlers.TestRateKey(u), StayRingHandlers.AnswerRateKey(u));
    }
}
