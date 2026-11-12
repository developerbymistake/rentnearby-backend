using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using RentNearBy.Infrastructure.Services.Retention;

namespace RentNearBy.Tests.Retention;

// Red until groups A, B and C have landed all 16 rules.
public class RetentionRuleCatalogTests
{
    private static readonly string[] ExpectedNames =
    {
        "NotificationEvents", "NotificationLogs", "TripPushLogs", "DeviceTokens", "AdminDeviceTokens", "Chat",
        "OutboxMessages", "TripReminderRuns", "TripShortlistItems", "TripOffers", "TripEnquiries", "TripEnquiryOrphans",
        "Sessions", "AdminSessions", "StayJobs", "BannerDismissals",
    };

    // Never auto-deleted (owner list), so no declarative rule may name them as its table.
    private static readonly string[] NeverDelete =
    {
        "PlanPurchases", "PaymentEvents", "PlaySubscriptions", "UserPlans", "CouponRedemptions", "Coupons", "ListingReports",
        "TripProblemLogs", "StayListings", "TaxiListings", "Users", "UserBlocks", "RoleChangeRequests", "Roles", "Trips",
        "SavedJourneys", "DistrictBanners", "DeletedAccountRecords",
    };

    private static IReadOnlyList<IRetentionRule> Rules() => RetentionRuleRegistration.Discover(typeof(RetentionRunner).Assembly);

    [Fact]
    public void Exactly_the_sixteen_contract_rules_exist()
        => Assert.Equal(ExpectedNames.OrderBy(x => x), Rules().Select(r => r.Name).OrderBy(x => x));

    [Fact]
    public void Names_are_unique_and_groups_known()
    {
        var rules = Rules();
        Assert.Equal(rules.Count, rules.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rules, r => Assert.Contains(r.Group, RetentionGroups.All));
        Assert.All(rules, r => Assert.True(r.DefaultDays >= 1));
    }

    [Fact]
    public void Every_rule_has_a_config_key_in_the_checked_in_appsettings()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RentNearBy.Api", "appsettings.json"));
        if (!File.Exists(path)) return;
        var keys = new ConfigurationBuilder().AddJsonFile(path).Build().GetSection("Retention:Rules").GetChildren().Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(Rules(), r => Assert.Contains(r.Name, keys));
    }

    [Fact]
    public void Declarative_rules_use_only_allowed_parameters_and_keep_protection_inside()
    {
        foreach (var rule in Rules().OfType<DeclarativeRetentionRule>())
        {
            var def = rule.Definition;
            foreach (Match m in Regex.Matches(def.EligibleSql, @"@\w+"))
                Assert.Contains(m.Value, RetentionParams.Allowed);
            Assert.False(string.IsNullOrWhiteSpace(def.EligibleSql));
            Assert.DoesNotContain(def.Table, NeverDelete);
            Assert.Contains("(" + def.EligibleSql + ")", rule.BuildDeleteSql());
            Assert.Contains("SKIP LOCKED", rule.BuildDeleteSql());
        }
    }

    [Fact]
    public void Rules_are_scheduled_in_the_contract_order()
    {
        var names = Rules().Select(r => r.Name).OrderBy(RetentionSchedule.OrderOf).ToList();
        Assert.True(names.IndexOf("TripOffers") < names.IndexOf("TripEnquiries"));
    }

    [Fact]
    public void Exempt_rules_are_exactly_the_rolling_ones()
        => Assert.Equal(new[] { "OutboxMessages", "TripReminderRuns", "TripShortlistItems" },
            Rules().Where(r => r.GuardExempt).Select(r => r.Name).OrderBy(x => x));

    [Fact]
    public void Banner_dismissals_default_off_and_orphans_use_180_days()
    {
        var rules = Rules().ToDictionary(r => r.Name);
        Assert.False(rules["BannerDismissals"].DefaultEnabled);
        Assert.Equal(180, rules["TripEnquiryOrphans"].DefaultDays);
        Assert.Equal(3, rules["OutboxMessages"].DefaultDays);
    }
}
