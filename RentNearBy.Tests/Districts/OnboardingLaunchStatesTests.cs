namespace RentNearBy.Tests.Districts;

public class OnboardingLaunchStatesTests
{
    [Fact]
    public void Names_ExactlyTwoLaunchStates()
        => Assert.Equal(
            new[] { "Uttarakhand", "Himachal Pradesh" },
            OnboardingLaunchStates.Names);

    [Theory]
    [InlineData("Uttarakhand")]
    [InlineData("Himachal Pradesh")]
    public void Contains_LaunchState_True(string state)
        => Assert.True(OnboardingLaunchStates.Contains(state));

    [Theory]
    [InlineData("Rajasthan")]
    [InlineData("Goa")]
    [InlineData("Kerala")]
    [InlineData("Jammu and Kashmir")]
    [InlineData("himachal pradesh")]
    [InlineData(" Goa")]
    [InlineData("Jammu & Kashmir")]
    [InlineData("Uttar Pradesh")]
    [InlineData(null)]
    [InlineData("")]
    public void Contains_CaseOrSpacingDiffers_False(string? state)
        => Assert.False(OnboardingLaunchStates.Contains(state));
}
