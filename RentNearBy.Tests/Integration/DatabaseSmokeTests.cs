using RentNearBy.Tests.Data;

namespace RentNearBy.Tests.Integration;

public class DatabaseSmokeTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task CanConnect_ConfiguredDatabase_ReturnsTrue()
    {
        await using var ctx = CreateContext();

        Assert.True(await ctx.Database.CanConnectAsync(TestContext.Current.CancellationToken));
    }
}
