using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Stays.Quotation;

// I-2: a plan key added after the database was first seeded (StayQuotationsPerMonth) reaches the seeded Free and Plus plans of an
// existing database with its seed value (Free 2 / Plus 30), so Plus is 30 and payment OFF is 30 without a DB reset. A row that
// already exists (seeded or admin-edited) is never touched; admin-created plans are not given rows.
public class QuotePlanKeyTopUpTests
{
    private static readonly Guid StayFree = DataSeeder.SeedPlanId(3, 1);
    private static readonly Guid StayPlus = DataSeeder.SeedPlanId(3, 2);
    private static readonly Guid TaxiFree = DataSeeder.SeedPlanId(4, 1);
    private static readonly Guid TaxiPlus = DataSeeder.SeedPlanId(4, 2);

    [Fact]
    public void Seed_plan_ids_are_the_catalog_ids()
    {
        Assert.Equal(OwnerPlanCatalog.StayPlusId, StayPlus);
        Assert.Equal(OwnerPlanCatalog.TaxiPlusId, TaxiPlus);
    }

    [Fact]
    public void Only_missing_keys_of_existing_seeded_plans_are_added_with_the_seed_value()
    {
        var all = new[] { StayFree, StayPlus, TaxiFree, TaxiPlus };
        var rows = all.SelectMany(id => PlanKeyCatalog.For(id == StayFree || id == StayPlus ? ListingKinds.Stay : ListingKinds.Taxi)
                .Select(k => (id, k.Key)))
            .Where(r => r.Key != PlanFeatureKeys.StayQuotationsPerMonth)
            .ToList();

        var missing = DataSeeder.MissingSeedPlanKeys(all, rows);

        Assert.Equal(2, missing.Count);
        Assert.All(missing, f => Assert.Equal(PlanFeatureKeys.StayQuotationsPerMonth, f.Key));
        Assert.Equal(2, missing.Single(f => f.PlanId == StayFree).Value);
        Assert.Equal(30, missing.Single(f => f.PlanId == StayPlus).Value);
        Assert.All(missing, f => Assert.NotEqual(Guid.Empty, f.Id));
    }

    [Fact]
    public void A_complete_or_absent_plan_gets_nothing()
    {
        var full = PlanKeyCatalog.For(ListingKinds.Stay).Select(k => (StayPlus, k.Key)).ToList();
        Assert.Empty(DataSeeder.MissingSeedPlanKeys([StayPlus], full));
        Assert.Empty(DataSeeder.MissingSeedPlanKeys([], []));
        Assert.DoesNotContain(DataSeeder.MissingSeedPlanKeys([TaxiPlus], []), f => f.Key == PlanFeatureKeys.StayQuotationsPerMonth);
    }

    [Fact]
    public void Seeder_tops_up_an_existing_catalog()
    {
        var source = File.ReadAllText(Path.Combine(RentNearBy.Tests.Trips.CompletionCounterGuardTests.Root(),
            "RentNearBy.Infrastructure", "Data", "DataSeeder.cs"));
        Assert.Contains("await TopUpSeedPlanKeysAsync(db);", source);
    }
}

[Collection(IntegrationCollection.Name)]
public class QuotePlanKeyTopUpIntegrationTests(TestDatabase db)
{
    [IntegrationFact]
    public async Task Existing_database_gets_free_two_plus_thirty_and_an_admin_value_is_kept()
    {
        await using var ctx = db.CreateContext();
        await DataSeeder.SeedPlansAsync(ctx);
        var ids = new[] { DataSeeder.SeedPlanId(3, 1), DataSeeder.SeedPlanId(3, 2) };
        var saved = await ctx.PlanFeatures.AsNoTracking()
            .Where(f => ids.Contains(f.PlanId) && f.Key == PlanFeatureKeys.StayQuotationsPerMonth).ToListAsync();
        try
        {
            // A database seeded before the key existed.
            await ctx.PlanFeatures.Where(f => ids.Contains(f.PlanId) && f.Key == PlanFeatureKeys.StayQuotationsPerMonth).ExecuteDeleteAsync();
            await DataSeeder.SeedPlansAsync(ctx);
            Assert.Equal(2, await ValueAsync(ctx, ids[0]));
            Assert.Equal(30, await ValueAsync(ctx, ids[1]));

            // An admin edit stays.
            await ctx.PlanFeatures.Where(f => f.PlanId == ids[1] && f.Key == PlanFeatureKeys.StayQuotationsPerMonth)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.Value, 12));
            await DataSeeder.TopUpSeedPlanKeysAsync(ctx);
            Assert.Equal(12, await ValueAsync(ctx, ids[1]));
            Assert.Equal(1, await ctx.PlanFeatures.CountAsync(f => f.PlanId == ids[1] && f.Key == PlanFeatureKeys.StayQuotationsPerMonth));
        }
        finally
        {
            await ctx.PlanFeatures.Where(f => ids.Contains(f.PlanId) && f.Key == PlanFeatureKeys.StayQuotationsPerMonth).ExecuteDeleteAsync();
            foreach (var row in saved) ctx.PlanFeatures.Add(new PlanFeature { Id = row.Id, PlanId = row.PlanId, Key = row.Key, Value = row.Value });
            await ctx.SaveChangesAsync();
        }
    }

    private static Task<int> ValueAsync(ApplicationDbContext ctx, Guid planId)
        => ctx.PlanFeatures.AsNoTracking().Where(f => f.PlanId == planId && f.Key == PlanFeatureKeys.StayQuotationsPerMonth)
            .Select(f => f.Value).SingleAsync();
}
