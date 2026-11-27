using System.Reflection;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Foundation;

// Contract 1.4 and 11.1: every key of PlanFeatureKeys.All has catalog info, a seed row per kind, a PlanFeatureValues property with
// From / Max / Fallback coverage, a PlanLimits field and a MyPlanLimitsDto field; the numbers equal the master table.
public class PlanKeyCatalogTests
{
    private static readonly Dictionary<string, string> LimitsAlias = new()
    {
        [PlanFeatureKeys.Priority] = nameof(PlanLimits.PriorityRank),
        [PlanFeatureKeys.Featured] = nameof(PlanLimits.IsFeatured),
    };

    [Fact]
    public void Catalog_lists_every_key_once_in_the_doc_order()
    {
        Assert.Equal(PlanFeatureKeys.All, PlanKeyCatalog.All.Select(k => k.Key));
        Assert.Equal(17, PlanKeyCatalog.All.Count);
        Assert.All(PlanFeatureKeys.All, k => Assert.Equal(k, PlanKeyCatalog.Of(k).Key));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanKeyCatalog.Of("Rooms"));
    }

    [Theory]
    [InlineData("ListingLimit", "Stored", 1, 1, 1, 1, 1, 1)]
    [InlineData("Priority", "Ranking", 0, 1, 0, 1, 0, 999)]
    [InlineData("Featured", "Ranking", 0, 1, 0, 1, 0, 1)]
    [InlineData("JobsPosts", "Limit", 1, 4, null, null, 0, 999)]
    [InlineData("Photos", "Limit", 2, 8, null, null, 1, 12)]
    [InlineData("RoomTypes", "Limit", 2, 6, null, null, 1, 8)]
    [InlineData("Routes", "Limit", null, null, 1, 5, 1, 10)]
    [InlineData("RingWeight", "Ranking", null, null, 1, 2, 1, 5)]
    [InlineData("CalendarEntries", "Limit", 4, 50, 4, 50, 0, 999)]
    [InlineData("BlockedRanges", "Limit", 1, 10, null, null, 0, 999)]
    [InlineData("UpcomingDays", "Limit", 15, 183, 15, 183, 1, 366)]
    [InlineData("MonthsAhead", "Limit", 0, 6, 0, 6, 0, 12)]
    [InlineData("MonthSummary", "Limit", 0, 1, 0, 1, 0, 1)]
    [InlineData("Reminders", "Limit", 0, 1, 0, 1, 0, 1)]
    [InlineData("SpecialPrices", "Limit", 1, 5, null, null, 0, 999)]
    [InlineData("DemandCount", "Stored", 0, 1, null, null, 0, 1)]
    [InlineData("StayQuotationsPerMonth", "Limit", 2, 30, null, null, 0, 999)]
    public void Numbers_equal_the_master_table(string key, string kind, int? stayFree, int? stayPlus, int? taxiFree, int? taxiPlus, int min, int max)
    {
        var info = PlanKeyCatalog.Of(key);
        Assert.Equal(kind, info.Kind.ToString());
        Assert.Equal((min, max), (info.Min, info.Max));
        Assert.Equal(stayFree, info.Stay?.Free);
        Assert.Equal(stayPlus, info.Stay?.Plus);
        Assert.Equal(taxiFree, info.Taxi?.Free);
        Assert.Equal(taxiPlus, info.Taxi?.Plus);
        Assert.Equal(info.Stay != null, info.AppliesTo(ListingKinds.Stay));
        Assert.Equal(info.Taxi != null, info.AppliesTo(ListingKinds.Taxi));
    }

    [Fact]
    public void Seed_values_sit_inside_the_range_and_plus_is_never_below_free_for_limit_keys()
    {
        foreach (var info in PlanKeyCatalog.All)
            foreach (var seed in new[] { info.Stay, info.Taxi }.Where(s => s != null))
            {
                Assert.InRange(seed!.Free, info.Min, info.Max);
                Assert.InRange(seed.Plus, info.Min, info.Max);
                Assert.True(seed.Plus >= seed.Free, info.Key);
            }
    }

    [Fact]
    public void Switches_are_exactly_featured_month_summary_reminders_and_demand_count()
        => Assert.Equal(
            [PlanFeatureKeys.Featured, PlanFeatureKeys.MonthSummary, PlanFeatureKeys.Reminders, PlanFeatureKeys.DemandCount],
            PlanKeyCatalog.All.Where(k => k.IsSwitch).Select(k => k.Key));

    [Fact]
    public void Labels_are_per_kind_for_calendar_entries_only()
    {
        Assert.Equal("Bookings he adds, active", PlanKeyCatalog.Of(PlanFeatureKeys.CalendarEntries).LabelFor(ListingKinds.Stay));
        Assert.Equal("Trips he adds, active", PlanKeyCatalog.Of(PlanFeatureKeys.CalendarEntries).LabelFor(ListingKinds.Taxi));
        Assert.All(PlanKeyCatalog.All.Where(k => k.Key != PlanFeatureKeys.CalendarEntries),
            k => Assert.Equal(k.LabelFor(ListingKinds.Stay), k.LabelFor(ListingKinds.Taxi)));
        Assert.Equal("Routes that ring", PlanKeyCatalog.Of(PlanFeatureKeys.Routes).Label);
    }

    [Fact]
    public void Plan_feature_values_have_one_property_per_key_and_get_build_round_trip()
    {
        var props = typeof(PlanFeatureValues).GetConstructors().Single().GetParameters().Select(p => p.Name!).ToList();
        Assert.Equal(PlanFeatureKeys.All, props);
        var i = 0;
        var built = PlanFeatureValues.Build(k => PlanKeyCatalog.Of(k).IsSwitch ? 1 : 10 + i++);
        Assert.All(PlanFeatureKeys.All, k => Assert.Equal(built.Get(k), PlanFeatureValues.Build(built.Get).Get(k)));
    }

    [Fact]
    public void From_reads_zero_for_a_missing_legacy_key_and_free_for_a_missing_new_key()
    {
        var v = PlanFeatureValues.From(new Dictionary<string, int>());
        Assert.All(PlanFeatureKeys.Legacy, k => Assert.Equal(0, v.Get(k)));
        Assert.All(PlanFeatureKeys.All.Except(PlanFeatureKeys.Legacy), k => Assert.Equal(PlanFeatureValues.Fallback.Get(k), v.Get(k)));
    }

    [Fact]
    public void Fallback_is_the_free_column()
    {
        var f = PlanFeatureValues.Fallback;
        Assert.Equal((1, 0, false, 1), (f.ListingLimit, f.Priority, f.Featured, f.JobsPosts));
        Assert.Equal((2, 2, 1, 1), (f.Photos, f.RoomTypes, f.Routes, f.RingWeight));
        Assert.Equal((4, 1, 15, 0, 1), (f.CalendarEntries, f.BlockedRanges, f.UpcomingDays, f.MonthsAhead, f.SpecialPrices));
        Assert.False(f.MonthSummary || f.Reminders || f.DemandCount);
        Assert.Equal(2, f.StayQuotationsPerMonth);
    }

    [Fact]
    public void Max_covers_every_key()
    {
        var low = PlanFeatureValues.Build(_ => 0);
        var high = PlanFeatureValues.Build(_ => 1);
        var max = PlanFeatureValues.Max(low, high);
        Assert.All(PlanFeatureKeys.All, k => Assert.Equal(1, max.Get(k)));
    }

    [Fact]
    public void Plan_limits_and_the_plans_me_dto_carry_every_key()
    {
        var limitsProps = typeof(PlanLimits).GetProperties().Select(p => p.Name).ToHashSet();
        var dtoProps = typeof(MyPlanLimitsDto).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.All(PlanFeatureKeys.All, k =>
        {
            Assert.Contains(LimitsAlias.GetValueOrDefault(k, k), limitsProps);
            Assert.Contains(k, dtoProps);
        });
    }

    [Fact]
    public void Plan_limits_get_reads_every_key()
    {
        var limits = PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), PaymentFeatureStates.On(), ListingKinds.Stay, null);
        Assert.All(PlanFeatureKeys.All, k => Assert.Equal(PlanKeyCatalog.Of(k).Stay?.Free ?? limits.Get(k), limits.Get(k)));
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.Get("Rooms"));
    }

    [Theory]
    [InlineData(ListingKinds.Stay)]
    [InlineData(ListingKinds.Taxi)]
    public void Seeder_rows_are_one_per_key_of_the_kind(string kind)
    {
        var free = OwnerPlanCatalog.Features(kind, plus: false);
        var plus = OwnerPlanCatalog.Features(kind, plus: true);
        Assert.Equal(PlanKeyCatalog.All.Where(k => k.AppliesTo(kind)).Select(k => k.Key), free.Keys);
        Assert.Equal(free.Keys, plus.Keys);
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Infrastructure", "Data", "DataSeeder.cs"));
        Assert.Contains("PlanKeyCatalog.For(kind)", source);
        Assert.Contains("key.SeedFor(kind)!.Free", source);
        Assert.Contains("key.SeedFor(kind)!.Plus", source);
    }

    [Theory]
    [InlineData(ListingKinds.Stay)]
    [InlineData(ListingKinds.Taxi)]
    [PlanKeyTest(PlanFeatureKeys.ListingLimit)]
    public void ListingLimit_IsOneOnEveryPlanAndWhileOff(string kind)
    {
        var snapshot = OwnerPlanCatalog.Seeded();
        var plusRow = new PlanEntitlementService.ActiveRow(Guid.NewGuid(), Guid.NewGuid(),
            kind == ListingKinds.Stay ? OwnerPlanCatalog.StayPlusId : OwnerPlanCatalog.TaxiPlusId, UserPlanSources.Prepaid,
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(10));
        Assert.Equal(1, PlanEntitlementService.Compute(snapshot, PaymentFeatureStates.On(), kind, null).ListingLimit);
        Assert.Equal(1, PlanEntitlementService.Compute(snapshot, PaymentFeatureStates.On(), kind, plusRow).ListingLimit);
        Assert.Equal(1, PlanEntitlementService.Compute(snapshot, PaymentFeatureStates.Off(), kind, null).ListingLimit);
        Assert.Equal(1, PlanEntitlementService.Compute(snapshot, PaymentFeatureStates.Off(), kind, plusRow).ListingLimit);
    }

    [Fact]
    [PlanKeyTest(PlanFeatureKeys.DemandCount)]
    public void DemandCount_is_stored_only_off_on_free_on_with_plus_and_stay_only()
    {
        var info = PlanKeyCatalog.Of(PlanFeatureKeys.DemandCount);
        Assert.Equal(PlanKeyKind.Stored, info.Kind);
        Assert.True(info.IsSwitch);
        Assert.Equal(new PlanKeySeed(0, 1), info.Stay);
        Assert.Null(info.Taxi);
        var snapshot = OwnerPlanCatalog.Seeded();
        Assert.False(PlanEntitlementService.Compute(snapshot, PaymentFeatureStates.On(), ListingKinds.Stay, null).DemandCount);
        // Stored keys take the Free value while payment is OFF.
        Assert.False(PlanEntitlementService.Compute(snapshot, PaymentFeatureStates.Off(), ListingKinds.Stay, null).DemandCount);
    }

    // Every plan key has exactly one enforcement test tagged [PlanKeyTest("Key")] (contract 13.16). The lanes add the tests, so
    // this runs explicitly until they have all landed: the gate runs it with "-explicit on" (or removes Explicit after the lanes).
    [Fact(Explicit = true)]
    public void Every_plan_key_has_one_named_enforcement_test()
    {
        var tagged = typeof(PlanKeyCatalogTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<PlanKeyTestAttribute>()?.Key)
            .Where(k => k != null)
            .GroupBy(k => k!)
            .ToDictionary(g => g.Key, g => g.Count());
        var missing = PlanFeatureKeys.All.Where(k => !tagged.ContainsKey(k)).ToList();
        var twice = tagged.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
        Assert.True(missing.Count == 0, "Keys without a [PlanKeyTest] test: " + string.Join(", ", missing));
        Assert.True(twice.Count == 0, "Keys with more than one [PlanKeyTest] test: " + string.Join(", ", twice));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }
}
