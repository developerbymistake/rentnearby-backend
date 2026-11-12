using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services.Retention.GroupA;

public sealed class GroupARetentionRules : IRetentionRuleSource
{
    const string RunningTrip =
        "EXISTS (SELECT 1 FROM \"Trips\" tr WHERE tr.\"OwnerUserId\" = t.\"UserId\" AND tr.\"Status\" <> 'Completed' AND (tr.\"EndDate\" IS NULL OR tr.\"EndDate\" >= @today))";

    const string OpenEnquiry =
        "EXISTS (SELECT 1 FROM \"TripEnquiries\" e WHERE (e.\"TravellerUserId\" = t.\"UserId\" OR e.\"OwnerUserId\" = t.\"UserId\") AND e.\"Status\" IN " + TripEnquiryStatuses.OpenSqlList + ")";

    const string TripPushLogProtection =
        "NOT (t.\"TripEnquiryId\" IS NOT NULL AND EXISTS (SELECT 1 FROM \"TripEnquiries\" e WHERE e.\"Id\" = t.\"TripEnquiryId\" AND e.\"Status\" IN " + TripEnquiryStatuses.OpenSqlList + ")) " +
        "AND NOT (t.\"TripId\" IS NOT NULL AND EXISTS (SELECT 1 FROM \"Trips\" tr WHERE tr.\"Id\" = t.\"TripId\" AND tr.\"Status\" <> 'Completed' AND (tr.\"EndDate\" IS NULL OR tr.\"EndDate\" >= @today)))";

    public static readonly RetentionRuleDefinition NotificationEvents = new(
        "NotificationEvents", RetentionGroups.Notifications, "NotificationEvents", "Id",
        "t.\"CreatedAt\" < @cutoff", "t.\"CreatedAt\"", 90);

    public static readonly RetentionRuleDefinition NotificationLogs = new(
        "NotificationLogs", RetentionGroups.Notifications, "NotificationLogs", "Id",
        "t.\"SentAt\" < @cutoff", "t.\"SentAt\"", 90);

    public static readonly RetentionRuleDefinition TripPushLogs = new(
        "TripPushLogs", RetentionGroups.Notifications, "TripPushLogs", "Id",
        "t.\"SentAt\" < @cutoff AND " + TripPushLogProtection, "t.\"SentAt\"", 90);

    public static readonly RetentionRuleDefinition DeviceTokens = new(
        "DeviceTokens", RetentionGroups.Devices, "DeviceTokens", "Id",
        "t.\"UpdatedAt\" < @cutoff AND (t.\"IsValid\" = false OR NOT (" + RunningTrip + " OR " + OpenEnquiry + "))",
        "t.\"UpdatedAt\"", 90);

    public static readonly RetentionRuleDefinition AdminDeviceTokens = new(
        "AdminDeviceTokens", RetentionGroups.Devices, "AdminDeviceTokens", "Id",
        "t.\"IsValid\" = false AND t.\"UpdatedAt\" < @cutoff", "t.\"UpdatedAt\"", 90);

    public static IReadOnlyList<RetentionRuleDefinition> Definitions { get; } =
        [NotificationEvents, NotificationLogs, TripPushLogs, DeviceTokens, AdminDeviceTokens];

    public IEnumerable<IRetentionRule> Create() => Definitions.Select(d => new DeclarativeRetentionRule(d));
}
