using System.Reflection;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.Regression;

// G4: Stay/Taxi plan ids, price ids and Play products survive the Rooms/Plots removal. Runs against
// RENTNEARBY_TEST_DB (migrated schema); seeds inside a transaction that is rolled back when the catalog is empty.
public class PlanSeedIdsGuardTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task SeededPlans_KeepStayAndTaxiIdsAndProducts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);

        if (!await ctx.Plans.AnyAsync(ct))
        {
            var seed = typeof(DataSeeder).GetMethod("SeedPlansAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            await (Task)seed.Invoke(null, [ctx])!;
        }

        var plans = await ctx.Plans.AsNoTracking().Include(p => p.Prices).ToListAsync(ct);

        AssertPlan(plans, "b1000000-0000-0000-0000-000000000301", ListingKinds.Stay, "STAY_FREE", isDefault: true, product: null);
        AssertPlan(plans, "b1000000-0000-0000-0000-000000000302", ListingKinds.Stay, "STAY_PLUS", isDefault: false, product: "stay_plus");
        AssertPlan(plans, "b1000000-0000-0000-0000-000000000401", ListingKinds.Taxi, "TAXI_FREE", isDefault: true, product: null);
        AssertPlan(plans, "b1000000-0000-0000-0000-000000000402", ListingKinds.Taxi, "TAXI_PLUS", isDefault: false, product: "taxi_plus");

        AssertPrices(plans, "b1000000-0000-0000-0000-000000000302", "03");
        AssertPrices(plans, "b1000000-0000-0000-0000-000000000402", "04");

        Assert.DoesNotContain(plans, p => p.FeatureKey is "Room" or "Plot");
        Assert.DoesNotContain(plans, p => p.PlayProductId is "room_plus" or "plot_plus");

        await tx.RollbackAsync(ct);
    }

    private static void AssertPlan(List<Plan> plans, string id, string feature, string code, bool isDefault, string? product)
    {
        var plan = Assert.Single(plans, p => p.Id == new Guid(id));
        Assert.Equal(feature, plan.FeatureKey);
        Assert.Equal(code, plan.Code);
        Assert.Equal(isDefault, plan.IsDefault);
        Assert.Equal(product, plan.PlayProductId);
    }

    private static void AssertPrices(List<Plan> plans, string planId, string feature)
    {
        var plan = Assert.Single(plans, p => p.Id == new Guid(planId));
        Assert.Equal(
            new[] { $"b2000000-0000-0000-0000-00000000{feature}01", $"b2000000-0000-0000-0000-00000000{feature}03", $"b2000000-0000-0000-0000-00000000{feature}12" }
                .Select(s => new Guid(s)),
            plan.Prices.OrderBy(p => p.CycleMonths).Select(p => p.Id));
        Assert.Equal(new short[] { 1, 3, 12 }, plan.Prices.OrderBy(p => p.CycleMonths).Select(p => p.CycleMonths));
    }
}
