using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services.Retention.GroupA;

namespace RentNearBy.Tests.Retention.GroupA;

public class GroupARuleSqlTests
{
    static RetentionRuleDefinition Def(string name) => GroupARetentionRules.Definitions.Single(d => d.Name == name);

    [Fact]
    public void Source_declares_the_five_group_a_rules_with_90_day_defaults()
    {
        Assert.Equal(
            ["NotificationEvents", "NotificationLogs", "TripPushLogs", "DeviceTokens", "AdminDeviceTokens"],
            GroupARetentionRules.Definitions.Select(d => d.Name));
        Assert.All(GroupARetentionRules.Definitions, d => Assert.Equal(90, d.DefaultDays));
        Assert.All(GroupARetentionRules.Definitions, d => Assert.True(d.DefaultEnabled));
        Assert.All(GroupARetentionRules.Definitions, d => Assert.False(d.GuardExempt));
        Assert.Equal(5, new GroupARetentionRules().Create().Count());
    }

    [Fact]
    public void Groups_follow_the_schedule_slots()
    {
        Assert.Equal(RetentionGroups.Notifications, Def("NotificationEvents").Group);
        Assert.Equal(RetentionGroups.Notifications, Def("NotificationLogs").Group);
        Assert.Equal(RetentionGroups.Notifications, Def("TripPushLogs").Group);
        Assert.Equal(RetentionGroups.Devices, Def("DeviceTokens").Group);
        Assert.Equal(RetentionGroups.Devices, Def("AdminDeviceTokens").Group);
    }

    [Fact]
    public void Events_and_logs_use_their_own_activity_column_only()
    {
        Assert.Equal("t.\"CreatedAt\" < @cutoff", Def("NotificationEvents").EligibleSql);
        Assert.Equal("t.\"SentAt\" < @cutoff", Def("NotificationLogs").EligibleSql);
        Assert.DoesNotContain("ReadAt", Def("NotificationEvents").EligibleSql);
    }

    [Fact]
    public void TripPushLogs_protect_open_enquiries_and_running_trips()
    {
        var sql = Def("TripPushLogs").EligibleSql;
        Assert.Contains("t.\"SentAt\" < @cutoff", sql);
        Assert.Contains("\"TripEnquiries\"", sql);
        Assert.Contains(TripEnquiryStatuses.OpenSqlList, sql);
        Assert.Contains("\"Trips\"", sql);
        Assert.Contains("tr.\"Status\" <> 'Completed'", sql);
        Assert.Contains("tr.\"EndDate\" IS NULL OR tr.\"EndDate\" >= @today", sql);
    }

    [Fact]
    public void DeviceTokens_dead_always_go_idle_valid_keep_when_user_has_running_trip_or_open_enquiry()
    {
        var sql = Def("DeviceTokens").EligibleSql;
        Assert.StartsWith("t.\"UpdatedAt\" < @cutoff", sql);
        Assert.Contains("t.\"IsValid\" = false OR NOT (", sql);
        Assert.Contains("tr.\"OwnerUserId\" = t.\"UserId\"", sql);
        Assert.Contains("e.\"TravellerUserId\" = t.\"UserId\" OR e.\"OwnerUserId\" = t.\"UserId\"", sql);
        Assert.Contains(TripEnquiryStatuses.OpenSqlList, sql);
    }

    [Fact]
    public void AdminDeviceTokens_only_invalid_ones()
    {
        var sql = Def("AdminDeviceTokens").EligibleSql;
        Assert.Contains("t.\"IsValid\" = false", sql);
        Assert.Contains("t.\"UpdatedAt\" < @cutoff", sql);
    }

    [Fact]
    public void Only_contract_parameters_are_used()
    {
        var allowed = new HashSet<string> { "@cutoff", "@cutoffDate", "@today", "@now", "@n" };
        foreach (var d in GroupARetentionRules.Definitions)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(d.EligibleSql, "@\\w+"))
                Assert.Contains(m.Value, allowed);
    }

    [Fact]
    public void Rules_never_target_tables_that_must_survive()
    {
        string[] never = ["PlanPurchases", "PaymentEvents", "PlaySubscriptions", "ListingReports", "Users", "TripProblemLogs", "StayListings", "TaxiListings"];
        Assert.All(GroupARetentionRules.Definitions, d => Assert.DoesNotContain(d.Table, never));
    }
}
