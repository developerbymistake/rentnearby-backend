using System.Text.RegularExpressions;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services.Retention.GroupC;

namespace RentNearBy.Tests.Retention.GroupC;

public class GroupCRuleSqlTests
{
    private static RetentionRuleDefinition Def(string name) => GroupCRetentionRules.Definitions.Single(d => d.Name == name);

    [Fact]
    public void Declares_exactly_the_four_group_c_rules_in_the_sessions_group()
    {
        Assert.Equal(["Sessions", "AdminSessions", "StayJobs", "BannerDismissals"], GroupCRetentionRules.Definitions.Select(d => d.Name));
        Assert.All(GroupCRetentionRules.Definitions, d => Assert.Equal(RetentionGroups.Sessions, d.Group));
        Assert.All(GroupCRetentionRules.Definitions, d => Assert.Equal(90, d.DefaultDays));
    }

    [Theory]
    [InlineData("Sessions", "Sessions")]
    [InlineData("AdminSessions", "AdminSessions")]
    public void Session_rules_never_match_a_live_session(string name, string table)
    {
        var d = Def(name);
        Assert.Equal(table, d.Table);
        Assert.Contains("NOT (t.\"IsActive\" AND t.\"ExpiresAt\" > @now)", d.EligibleSql);
        Assert.Contains("t.\"ExpiresAt\" < @cutoff", d.EligibleSql);
        Assert.Contains("t.\"LoggedOutAt\" < @cutoff", d.EligibleSql);
        Assert.False(d.GuardExempt);
    }

    [Fact]
    public void StayJobs_only_closed_and_protects_unpushed_recent_jobs()
    {
        var d = Def("StayJobs");
        Assert.Contains("t.\"Status\" = 'Closed'", d.EligibleSql);
        Assert.DoesNotContain("'Active'", d.EligibleSql);
        Assert.Contains("COALESCE(t.\"ClosedAt\", t.\"UpdatedAt\") < @cutoff", d.EligibleSql);
        Assert.Contains("NOT (t.\"SeekerPushedAt\" IS NULL AND t.\"CreatedAt\" >= @cutoff)", d.EligibleSql);
    }

    [Fact]
    public void BannerDismissals_is_off_by_default_with_composite_key_and_inactive_banner_check()
    {
        var d = Def("BannerDismissals");
        Assert.False(d.DefaultEnabled);
        Assert.Equal(["UserId", "BannerId"], d.KeyColumns);
        Assert.Contains("b.\"IsActive\" = false", d.EligibleSql);
        Assert.Contains("t.\"DismissedAt\" < @cutoff", d.EligibleSql);
    }

    [Fact]
    public void Only_allowed_parameters_are_used_and_other_rules_default_on()
    {
        var allowed = new[] { "@cutoff", "@cutoffDate", "@today", "@now", "@n" };
        foreach (var d in GroupCRetentionRules.Definitions)
        {
            var used = Regex.Matches(d.EligibleSql, "@[a-zA-Z]+").Select(m => m.Value).Distinct();
            Assert.All(used, p => Assert.Contains(p, allowed));
            if (d.Name != "BannerDismissals") Assert.True(d.DefaultEnabled);
        }
    }

    [Fact]
    public void Source_creates_one_rule_per_definition()
    {
        var rules = new GroupCRetentionRules().Create().ToList();
        Assert.Equal(GroupCRetentionRules.Definitions.Select(d => d.Name), rules.Select(r => r.Name));
        Assert.False(rules.Single(r => r.Name == "BannerDismissals").DefaultEnabled);
    }
}
