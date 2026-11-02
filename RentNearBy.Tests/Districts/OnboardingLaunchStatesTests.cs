namespace RentNearBy.Tests.Districts;

public class OnboardingLaunchStatesTests
{
    [Fact]
    public void Names_ExactlySixLaunchStates()
        => Assert.Equal(
            new[] { "Uttarakhand", "Himachal Pradesh", "Rajasthan", "Goa", "Kerala", "Jammu and Kashmir" },
            OnboardingLaunchStates.Names);

    [Theory]
    [InlineData("Uttarakhand")]
    [InlineData("Himachal Pradesh")]
    [InlineData("Rajasthan")]
    [InlineData("Goa")]
    [InlineData("Kerala")]
    [InlineData("Jammu and Kashmir")]
    public void Contains_LaunchState_True(string state)
        => Assert.True(OnboardingLaunchStates.Contains(state));

    [Theory]
    [InlineData("himachal pradesh")]
    [InlineData(" Goa")]
    [InlineData("Jammu & Kashmir")]
    [InlineData("Uttar Pradesh")]
    [InlineData(null)]
    [InlineData("")]
    public void Contains_CaseOrSpacingDiffers_False(string? state)
        => Assert.False(OnboardingLaunchStates.Contains(state));
}
