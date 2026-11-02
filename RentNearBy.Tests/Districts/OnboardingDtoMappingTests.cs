using Mapster;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Districts;

public class OnboardingDtoMappingTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Adapt_District_CarriesBusinessOnboardingEnabled(bool enabled, bool isActive)
    {
        var dto = new District { Id = Guid.NewGuid(), Name = "Nainital", StateName = "Uttarakhand", IsActive = isActive, BusinessOnboardingEnabled = enabled }
            .Adapt<DistrictDto>();

        Assert.Equal(enabled, dto.BusinessOnboardingEnabled);
        Assert.Equal(isActive, dto.IsActive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Adapt_StayListing_CarriesEverApproved(bool everApproved)
        => Assert.Equal(everApproved, new StayListing
        {
            Id = Guid.NewGuid(), EverApproved = everApproved,
            RegistrationTypeId = Guid.NewGuid(), RegistrationTypeName = "GST", RegistrationNumber = "X1",
        }.Adapt<StayOwnerDto>().EverApproved);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Adapt_TaxiListing_CarriesEverApproved(bool everApproved)
        => Assert.Equal(everApproved, new TaxiListing
        {
            Id = Guid.NewGuid(), EverApproved = everApproved,
            DrivingLicenceNumberEnc = "v1.x.y", DateOfBirth = new DateOnly(1990, 1, 1),
        }.Adapt<TaxiOwnerDto>().EverApproved);
}
