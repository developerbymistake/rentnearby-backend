using System.Text.RegularExpressions;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services.Retention.GroupB;

namespace RentNearBy.Tests.Retention.GroupB;

public class GroupBRuleSqlTests
{
    private static RetentionRuleDefinition Rule(string name) => GroupBRetentionRules.Definitions.Single(d => d.Name == name);

    private static readonly string[] Allowed = ["cutoff", "cutoffDate", "today", "now", "n"];

    [Fact]
    public void Rules_run_in_contract_order_with_defaults()
    {
        Assert.Equal(
            ["OutboxMessages", "TripReminderRuns", "TripShortlistItems", "TripOffers", "TripEnquiries", "TripEnquiryOrphans"],
            GroupBRetentionRules.Definitions.Select(d => d.Name));
        Assert.Equal([3, 30, 30, 90, 90, 180], GroupBRetentionRules.Definitions.Select(d => d.DefaultDays));
        Assert.All(GroupBRetentionRules.Definitions, d => Assert.True(d.DefaultEnabled));
    }

    [Fact]
    public void Housekeeping_rules_are_guard_exempt_and_enquiry_rules_are_not()
    {
        Assert.Equal(
            [true, true, true, false, false, false],
            GroupBRetentionRules.Definitions.Select(d => d.GuardExempt));
        Assert.All(GroupBRetentionRules.Definitions.Take(3), d => Assert.Equal(RetentionGroups.TripHousekeeping, d.Group));
        Assert.All(GroupBRetentionRules.Definitions.Skip(3), d => Assert.Equal(RetentionGroups.TripEnquiries, d.Group));
    }

    [Fact]
    public void Offers_run_before_enquiries()
    {
        var names = GroupBRetentionRules.Definitions.Select(d => d.Name).ToList();
        Assert.True(names.IndexOf("TripOffers") < names.IndexOf("TripEnquiries"));
    }

    [Fact]
    public void Only_contract_parameters_are_used()
    {
        foreach (var d in GroupBRetentionRules.Definitions)
            foreach (Match m in Regex.Matches(d.EligibleSql, @"@(\w+)"))
                Assert.Contains(m.Groups[1].Value, Allowed);
    }

    [Fact]
    public void Outbox_never_touches_unpublished_rows()
    {
        var sql = Rule("OutboxMessages").EligibleSql;
        Assert.Contains("\"PublishedAt\" IS NOT NULL", sql);
        Assert.Contains("\"PublishedAt\" < @cutoff", sql);
    }

    [Fact]
    public void Reminder_runs_use_the_date_cutoff()
        => Assert.Contains("\"RunDate\" < @cutoffDate", Rule("TripReminderRuns").EligibleSql);

    [Fact]
    public void Shortlist_never_matches_Active_and_keeps_running_trips()
    {
        var sql = Rule("TripShortlistItems").EligibleSql;
        Assert.DoesNotContain("'Active'", sql);
        Assert.Contains("'Closed','Booked'", sql);
        Assert.Contains("\"ClosesAt\" < @cutoff", sql);
        Assert.Contains("FROM \"Trips\"", sql);
        Assert.Contains("tr.\"EndDate\" IS NULL OR tr.\"EndDate\" >= @today", sql);
    }

    [Fact]
    public void Offers_rule_excludes_Pending_and_Accepted_and_checks_the_parent_enquiry()
    {
        var sql = Rule("TripOffers").EligibleSql;
        Assert.Contains("t.\"Status\" IN ('Expired','Declined','Superseded')", sql);
        Assert.DoesNotContain("'Accepted'", sql);
        Assert.DoesNotContain("'Pending'", sql.Replace("r.\"Status\" = 'Pending'", ""));
        Assert.Contains("e.\"UpdatedAt\" < @cutoff", sql);
        Assert.Contains("GREATEST(t.\"CreatedAt\", COALESCE(t.\"RespondedAt\", t.\"CreatedAt\")) < @cutoff", sql);
        Assert.Contains("\"AcceptedOfferId\" IS NULL", sql);
    }

    [Theory]
    [InlineData("TripOffers")]
    [InlineData("TripEnquiries")]
    [InlineData("TripEnquiryOrphans")]
    public void Enquiry_protection_is_inside_the_eligible_sql(string name)
    {
        var sql = Rule(name).EligibleSql;
        foreach (var token in new[]
        {
            "\"AdvanceStatus\" = 'None'", "\"ConfirmedByTravellerAt\" IS NULL", "\"ConfirmedByOwnerAt\" IS NULL", "\"BookedByTravellerAt\" IS NULL",
            "\"AdvanceReceivedAt\" IS NULL", "\"AcceptedOfferId\" IS NULL", "FROM \"ListingReports\"", "r.\"Status\" = 'Pending'",
            "r.\"ResolvedAt\" >= @cutoff", "FROM \"TripProblemLogs\"", "FROM \"Trips\" tr", "tr.\"EndDate\" >= @today",
            "o.\"StepKey\" IS NOT DISTINCT FROM", "'ConfirmedByBoth','FareSent','BookedByTraveller'",
        })
            Assert.Contains(token, sql);
    }

    [Fact]
    public void Closed_set_never_includes_open_or_problem_failures()
    {
        var closed = TripRetentionSql.ClosedEnquiry("t");
        foreach (var open in new[] { "Asked", "Offered", "Countered", "ConfirmedByBoth", "FareSent", "BookedByTraveller" })
            Assert.DoesNotContain($"'{open}'", closed);
        Assert.DoesNotContain("Problem", closed);
        Assert.Contains("'DriverDenied','HoldExpired','ListingRemoved'", closed);
    }

    [Fact]
    public void Open_status_list_matches_the_app_constants()
    {
        var expected = string.Join(",", TripEnquiryStatuses.Open.Select(s => $"'{s}'"));
        Assert.Equal(expected, TripRetentionSql.OpenStatuses);
    }

    [Fact]
    public void Enquiry_rules_age_by_last_activity_and_orphans_need_no_trip()
    {
        Assert.Contains("t.\"UpdatedAt\" < @cutoff", Rule("TripEnquiries").EligibleSql);
        var orphan = Rule("TripEnquiryOrphans").EligibleSql;
        Assert.Contains("t.\"TripId\" IS NULL", orphan);
        Assert.Contains("t.\"UpdatedAt\" < @cutoff", orphan);
        Assert.Equal("TripEnquiries", Rule("TripEnquiryOrphans").Table);
    }

    [Fact]
    public void Source_creates_one_rule_per_definition()
    {
        var rules = new GroupBRetentionRules().Create().ToList();
        Assert.Equal(GroupBRetentionRules.Definitions.Select(d => d.Name), rules.Select(r => r.Name));
    }
}
