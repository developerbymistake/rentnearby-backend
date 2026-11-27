using System.Text.RegularExpressions;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Retention.Owner;

namespace RentNearBy.Tests.Owner.Be1;

// Contract 5.1 nightly clean-up (seven declarative rules of group D) and the purge seam (6.2 step 6, AU-26).
public class OwnerRetentionAndPurgeTests
{
    private static readonly (string Name, string Table, string End, int Days)[] Expected =
    [
        ("OwnerCalendarEntries", "CalendarEntries", "DateTo", 90),
        ("StayBlockedRanges", "StayBlockedRanges", "DateTo", 90),
        ("StaySpecialPrices", "StaySpecialPrices", "DateTo", 30),
        ("FestivalReminderSent", "FestivalReminderSent", "FestivalDateTo", 30),
        ("OwnerReminderRuns", "OwnerReminderRuns", "RunDate", 30),
        ("OwnerWeeklyStats", "OwnerWeeklyStats", "Day", 56),
        ("TaxiDayOffs", "TaxiDayOffs", "Date", 1),
    ];

    [Fact]
    public void Seven_rules_in_group_d_with_their_days_and_end_dates()
    {
        var defs = OwnerRetentionRules.Definitions.ToDictionary(d => d.Name);
        Assert.Equal(Expected.Length, defs.Count);
        foreach (var (name, table, end, days) in Expected)
        {
            var d = defs[name];
            Assert.Equal(RetentionGroups.OwnerCalendar, d.Group);
            Assert.Equal(table, d.Table);
            Assert.Equal(days, d.DefaultDays);
            Assert.Equal($"t.\"{end}\" < @cutoffDate", d.EligibleSql);
            Assert.Equal(end, d.OrderBy);
        }
    }

    // One row per day with a 1-day window: a month start or a skipped night passes the 20 percent guard, so it is exempt.
    [Fact]
    public void Only_the_day_off_rule_is_guard_exempt_in_group_d()
        => Assert.Equal(new[] { "TaxiDayOffs" },
            OwnerRetentionRules.Definitions.Where(d => d.GuardExempt).Select(d => d.Name).ToArray());

    [Fact]
    public void Rules_are_discovered_in_the_contract_order_and_use_only_allowed_parameters()
    {
        var rules = RetentionRuleRegistration.Discover(typeof(RetentionRunner).Assembly).OfType<DeclarativeRetentionRule>()
            .Where(r => r.Group == RetentionGroups.OwnerCalendar).ToList();
        // Group D = the seven owner calendar rules plus the stay quotation issue purge (its own rule source, last in the slot).
        string[] groupD = [.. Expected.Select(e => e.Name), "StayQuoteIssues"];
        Assert.Equal(groupD.OrderBy(n => n), rules.Select(r => r.Name).OrderBy(n => n));
        var ordered = rules.Select(r => r.Name).OrderBy(RetentionSchedule.OrderOf).ToList();
        Assert.Equal(groupD, ordered);
        Assert.True(RetentionSchedule.OrderOf("OwnerCalendarEntries") > RetentionSchedule.OrderOf("Chat"));
        foreach (var rule in rules)
        {
            foreach (Match m in Regex.Matches(rule.Definition.EligibleSql, @"@\w+")) Assert.Contains(m.Value, RetentionParams.Allowed);
            Assert.Contains("ORDER BY", rule.BuildDeleteSql());
            Assert.Contains("FOR UPDATE SKIP LOCKED", rule.BuildDeleteSql());
        }
    }

    [Fact]
    public void Composite_key_tables_delete_by_their_whole_key()
    {
        var rules = OwnerRetentionRules.Definitions.ToDictionary(d => d.Name, d => new DeclarativeRetentionRule(d));
        Assert.Contains("(t.\"UserId\", t.\"FestivalDateId\") IN", rules["FestivalReminderSent"].BuildDeleteSql());
        Assert.Contains("(t.\"RunDate\", t.\"ListingKind\") IN", rules["OwnerReminderRuns"].BuildDeleteSql());
        Assert.Contains("(t.\"ListingKind\", t.\"ListingId\", t.\"Day\") IN", rules["OwnerWeeklyStats"].BuildDeleteSql());
        Assert.Contains("(t.\"TaxiListingId\", t.\"Date\") IN", rules["TaxiDayOffs"].BuildDeleteSql());
        Assert.Contains("t.\"Id\" IN", rules["OwnerCalendarEntries"].BuildDeleteSql());
    }

    [Fact]
    public void Purge_names_every_purge_table_of_the_coverage_list_and_keeps_the_order()
    {
        var stay = OwnerListingPurge.Statements(ListingKinds.Stay);
        Assert.Collection(stay,
            s => Assert.Contains("\"CalendarEntries\"", s),
            s => Assert.Contains("\"StayBlockedRanges\"", s),
            s => Assert.Contains("\"StaySpecialPrices\"", s),
            s => Assert.Contains("\"OwnerWeeklyStats\"", s));
        var taxi = OwnerListingPurge.Statements(ListingKinds.Taxi);
        Assert.Collection(taxi,
            s => Assert.Contains("\"CalendarEntries\"", s),
            s => Assert.Contains("\"TaxiDayOffs\"", s),
            s => Assert.Contains("\"OwnerWeeklyStats\"", s));
        Assert.All(stay.Concat(taxi), s => Assert.Contains("= ANY(@ids)", s));
        var purgeTables = new[] { "CalendarEntries", "StayBlockedRanges", "StaySpecialPrices", "OwnerWeeklyStats", "TaxiDayOffs" };
        foreach (var t in purgeTables)
            Assert.True(AccountDeletionCoverage.Tables.Single(c => c.Table == t).Mode.HasFlag(DeletionModes.Purge), t);
    }

    [Fact]
    public async Task Purge_with_no_listing_runs_nothing()
        => await new OwnerListingPurge(null!).PurgeAsync(ListingKinds.Stay, [], TestContext.Current.CancellationToken);
}
