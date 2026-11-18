using RentNearBy.Core.Models;

namespace RentNearBy.Tests.TaxiRequests;

public class DemoSeedGateTests
{
    [Theory]
    [InlineData("Development", null, true)]
    [InlineData("development", "false", true)]
    [InlineData("Staging", null, false)]
    [InlineData("Staging", "false", false)]
    [InlineData("Staging", "true", true)]
    [InlineData("Staging", "TRUE", true)]
    [InlineData("Staging", "yes", false)]
    [InlineData("Production", null, false)]
    public void Demo_seed_runs_only_in_development(string env, string? config, bool expected)
        => Assert.Equal(expected, DemoSeedGate.ShouldRun(env, config));

    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("")]
    [InlineData(null)]
    public void Demo_seed_never_runs_in_production_even_when_enabled(string? env)
        => Assert.False(DemoSeedGate.ShouldRun(env, "true"));
}
