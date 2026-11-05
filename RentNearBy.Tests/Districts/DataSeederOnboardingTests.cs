using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Districts;

public class DataSeederOnboardingTests
{
    [Theory]
    [InlineData(" Uttarakhand ")]
    [InlineData("Himachal Pradesh")]
    public void SeedOnboardingFlag_LaunchStateTrimmed_True(string state)
        => Assert.True(DataSeeder.SeedOnboardingFlag(state));

    [Theory]
    [InlineData("Punjab")]
    [InlineData("Rajasthan")]
    [InlineData(" Goa ")]
    [InlineData("Kerala")]
    [InlineData("Jammu and Kashmir")]
    public void SeedOnboardingFlag_OtherState_False(string state)
        => Assert.False(DataSeeder.SeedOnboardingFlag(state));
}
