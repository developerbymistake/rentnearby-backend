namespace RentNearBy.Tests.Districts;

public class BusinessOnboardingRulesTests
{
    private static readonly Guid DistrictA = Guid.Parse("0d15e1c7-0000-0000-0000-00000000000a");
    private static readonly Guid DistrictB = Guid.Parse("0d15e1c7-0000-0000-0000-00000000000b");
    private const decimal Lat = 30.316500m;
    private const decimal Lng = 78.032200m;

    [Theory]
    [InlineData(false, false, false, false, OnboardingCheck.DistrictNotFound)]
    [InlineData(false, true, true, true, OnboardingCheck.DistrictNotFound)]
    [InlineData(true, false, false, false, OnboardingCheck.DistrictInactive)]
    [InlineData(true, false, true, true, OnboardingCheck.DistrictInactive)]
    [InlineData(true, true, false, false, OnboardingCheck.OutsideDistrict)]
    [InlineData(true, true, false, true, OnboardingCheck.OutsideDistrict)]
    [InlineData(true, true, true, false, OnboardingCheck.OnboardingClosed)]
    [InlineData(true, true, true, true, OnboardingCheck.Ok)]
    public void Decide_Matrix(bool found, bool isActive, bool inside, bool enabled, OnboardingCheck expected)
        => Assert.Equal(expected, BusinessOnboardingRules.Decide(found, isActive, inside, enabled));

    [Fact]
    public void PinMoved_WithinTolerance_False()
        => Assert.False(BusinessOnboardingRules.PinMoved(Lat, Lng, Lat + 0.0000005m, Lng - 0.0000005m));

    [Fact]
    public void PinMoved_BeyondTolerance_True()
    {
        Assert.True(BusinessOnboardingRules.PinMoved(Lat, Lng, Lat + 0.00001m, Lng));
        Assert.True(BusinessOnboardingRules.PinMoved(Lat, Lng, Lat, Lng - 0.00001m));
    }

    [Fact]
    public void PinMoved_NoStoredPin_True()
    {
        Assert.True(BusinessOnboardingRules.PinMoved(null, Lng, Lat, Lng));
        Assert.True(BusinessOnboardingRules.PinMoved(Lat, null, Lat, Lng));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void NeedsFlagCheck_GrandfatherMatrix(bool everApproved, bool districtChanged, bool pinMoved, bool expected)
    {
        var newDistrict = districtChanged ? DistrictB : DistrictA;
        var newLat = pinMoved ? Lat + 0.001m : Lat;

        Assert.Equal(expected,
            BusinessOnboardingRules.NeedsFlagCheck(everApproved, DistrictA, newDistrict, Lat, Lng, newLat, Lng));
    }

    [Fact]
    public void NeedsFlagCheck_EverApprovedNoStoredPin_True()
        => Assert.True(BusinessOnboardingRules.NeedsFlagCheck(true, DistrictA, DistrictA, null, Lng, Lat, Lng));

    [Fact]
    public void NeedsFlagCheck_NewRowNoStoredDistrict_True()
        => Assert.True(BusinessOnboardingRules.NeedsFlagCheck(false, null, DistrictA, null, null, Lat, Lng));

    [Fact]
    public void NeedsFlagCheckAtSubmit_NeverApproved_True()
        => Assert.True(BusinessOnboardingRules.NeedsFlagCheckAtSubmit(false));

    [Fact]
    public void NeedsFlagCheckAtSubmit_EverApproved_False()
        => Assert.False(BusinessOnboardingRules.NeedsFlagCheckAtSubmit(true));

    [Theory]
    [InlineData(OnboardingCheck.DistrictInactive)]
    [InlineData(OnboardingCheck.OutsideDistrict)]
    [InlineData(OnboardingCheck.OnboardingClosed)]
    public void ForSave_EverApprovedUnchanged_SkipsWholeGate(OnboardingCheck check)
        => Assert.Equal(OnboardingCheck.Ok,
            BusinessOnboardingRules.ForSave(check, true, DistrictA, DistrictA, Lat, Lng, Lat, Lng));

    [Theory]
    [InlineData(OnboardingCheck.DistrictInactive)]
    [InlineData(OnboardingCheck.OutsideDistrict)]
    [InlineData(OnboardingCheck.OnboardingClosed)]
    public void ForSave_EverApprovedPinMoved_KeepsCheck(OnboardingCheck check)
        => Assert.Equal(check,
            BusinessOnboardingRules.ForSave(check, true, DistrictA, DistrictA, Lat, Lng, Lat + 0.001m, Lng));

    [Theory]
    [InlineData(OnboardingCheck.DistrictInactive)]
    [InlineData(OnboardingCheck.OutsideDistrict)]
    [InlineData(OnboardingCheck.OnboardingClosed)]
    public void ForSave_NeverApprovedUnchanged_KeepsCheck(OnboardingCheck check)
        => Assert.Equal(check,
            BusinessOnboardingRules.ForSave(check, false, DistrictA, DistrictA, Lat, Lng, Lat, Lng));

    [Theory]
    [InlineData(OnboardingCheck.DistrictNotFound, true)]
    [InlineData(OnboardingCheck.DistrictInactive, true)]
    [InlineData(OnboardingCheck.OutsideDistrict, true)]
    [InlineData(OnboardingCheck.OnboardingClosed, false)]
    [InlineData(OnboardingCheck.Ok, false)]
    public void IsOutsideDistrict_OnlyNonFlagFailures(OnboardingCheck check, bool expected)
        => Assert.Equal(expected, BusinessOnboardingRules.IsOutsideDistrict(check));

    [Fact]
    public void OnboardingClosed_TypeAndMessage()
    {
        Assert.Equal("OnboardingClosed", BusinessOnboardingRules.OnboardingClosedType);
        Assert.Equal("We can't take new listings in this area right now.", BusinessOnboardingRules.OnboardingClosedMessage);
    }
}
