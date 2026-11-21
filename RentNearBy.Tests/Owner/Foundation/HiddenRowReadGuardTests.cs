using System.Text.RegularExpressions;
using RentNearBy.Tests.TaxiRequests;

namespace RentNearBy.Tests.Owner.Foundation;

// Contract 3.3 / 11.5: rows hidden by the plan are never read as visible. LINQ reads go through the HiddenByPlan query filter;
// every raw SQL statement over StayRooms, StayPhotos or TaxiRoutes is in a file that filters on HiddenByPlan, or in the
// "sees all" list; every IgnoreQueryFilters() call is in the allow-list of 3.3.
public class HiddenRowReadGuardTests
{
    private static readonly Regex SqlOverHiddenTables = new(@"(?i)\b(FROM|JOIN|UPDATE|INTO)\s+\\?""(StayRooms|StayPhotos|TaxiRoutes)\\?""", RegexOptions.Compiled);

    // Files whose raw SQL sees hidden rows on purpose (3.3 "who sees all rows"). Lanes add their own sees-all files here.
    private static readonly string[] SeesAllSql =
    [
        "TaxiRouteCityKeys.cs",
        "PlanRows.cs", "PlanVisibleService.cs", "PlanLimitsReconcile.cs", "PlanSlotService.cs", "OwnerHiddenCounts.cs",
        "OwnerListingPurge.cs", "ListingPurgeService.cs",
    ];

    // 3.3 allow-list of IgnoreQueryFilters(): owner hidden arrays (M1, M2), choose-what-stays, plan effects and reconcile, purge,
    // admin review, the city key backfill, the admin taxi health count and the driver readiness count.
    private static readonly string[] MayLiftFilter =
    [
        "OwnerFeatureModel.cs", "ListingPurgeService.cs", "OwnerListingPurge.cs", "TaxiRouteCityKeys.cs", "AdminTaxiRequestHandlers.cs", "TaxiDriverViews.cs",
        "StayHandlers.cs", "TaxiHandlers.cs", "PlanRows.cs", "PlanVisibleService.cs", "PlanLimitsReconcile.cs", "PlanSlotService.cs", "OwnerHiddenCounts.cs",
        "AdminStayHandlers.cs", "AdminTaxiHandlers.cs", "AdminStayEditHandlers.cs", "AdminUserPlanHandlers.cs", "StayPendingEditService.cs",
        "StayHubReader.cs", "TaxiHubReader.cs",
    ];

    [Fact]
    public void Raw_sql_over_hidden_tables_filters_hidden_rows_or_sees_all_on_purpose()
    {
        var bad = TaxiSource.ProductionFiles()
            .Where(f => SqlOverHiddenTables.IsMatch(File.ReadAllText(f)))
            .Where(f => !SeesAllSql.Contains(Path.GetFileName(f)) && !File.ReadAllText(f).Contains("HiddenByPlan", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(bad.Count == 0, "Raw SQL over StayRooms / StayPhotos / TaxiRoutes without a HiddenByPlan predicate: " + string.Join(", ", bad));
    }

    [Fact]
    public void The_known_raw_sql_sites_carry_the_predicate()
    {
        foreach (var (folder, file) in new[]
        {
            ("Repositories", "NearbyFeedQuery.cs"), ("Repositories", "SavedJourneyRepository.cs"), ("Repositories", "StayListingRepository.cs"),
            ("Repositories", "TaxiListingRepository.cs"), ("Repositories", "TaxiWaysNetworkRepository.cs"),
        })
            Assert.Contains("HiddenByPlan", TaxiSource.Read("RentNearBy.Infrastructure", folder, file));
        var candidate = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiCandidateQuery.cs");
        Assert.Equal(2, Regex.Matches(candidate, @"NOT p?rt\.""HiddenByPlan""").Count);
        Assert.Contains("NOT rt.\"HiddenByPlan\"", TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "Matching", "MatcherSql.cs"));
        Assert.Equal(2, Regex.Matches(TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiPickupPlaces.cs"), @"NOT rt\.""HiddenByPlan""").Count);
        Assert.Equal(4, Regex.Matches(TaxiSource.Read("RentNearBy.Infrastructure", "Repositories", "StayListingRepository.cs"), @"NOT (p|r)\.""HiddenByPlan""").Count);
    }

    [Fact]
    public void Ignore_query_filters_only_in_the_allow_list()
    {
        var bad = TaxiSource.ProductionFiles()
            .Where(f => File.ReadAllText(f).Contains("IgnoreQueryFilters(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(n => !MayLiftFilter.Contains(n))
            .ToList();
        Assert.True(bad.Count == 0, "IgnoreQueryFilters() outside the allow-list of contract 3.3: " + string.Join(", ", bad));
    }
}
