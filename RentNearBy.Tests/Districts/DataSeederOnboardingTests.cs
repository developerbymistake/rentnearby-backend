using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Districts;

public class DataSeederOnboardingTests
{
    [Fact]
    public void SeedOnboardingFlag_LaunchStateTrimmed_True()
        => Assert.True(DataSeeder.SeedOnboardingFlag(" Kerala "));

    [Fact]
    public void SeedOnboardingFlag_OtherState_False()
        => Assert.False(DataSeeder.SeedOnboardingFlag("Punjab"));
}
