namespace RentNearBy.Tests.Data;

// The one fixture type tests depend on. Where the database comes from is decided only here.
public sealed class TestDatabase : DatabaseFixture
{
    public const string EnvVar = "RENTNEARBY_TEST_DB";

    public const string SkipReason = "Set " + EnvVar + " to run";

    public static bool IsAvailable => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar));

    protected override Task<string?> StartAsync()
        => Task.FromResult(IsAvailable ? Environment.GetEnvironmentVariable(EnvVar) : null);
}
