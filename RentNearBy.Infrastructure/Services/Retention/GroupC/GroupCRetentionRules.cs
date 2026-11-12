using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services.Retention.GroupC;

// Group C: sessions, stay jobs, banner dismissals. Trips have no soft delete (TripHandlers.Delete hard-deletes), so no Trips rule.
public sealed class GroupCRetentionRules : IRetentionRuleSource
{
    // A live session (active and not expired) can never match.
    private const string SessionEligible =
        "NOT (t.\"IsActive\" AND t.\"ExpiresAt\" > @now) AND (t.\"ExpiresAt\" < @cutoff OR (t.\"IsActive\" = false AND t.\"LoggedOutAt\" < @cutoff))";

    private const string StayJobEligible =
        "t.\"Status\" = 'Closed' AND COALESCE(t.\"ClosedAt\", t.\"UpdatedAt\") < @cutoff AND NOT (t.\"SeekerPushedAt\" IS NULL AND t.\"CreatedAt\" >= @cutoff)";

    private const string BannerDismissalEligible =
        "t.\"DismissedAt\" < @cutoff AND EXISTS (SELECT 1 FROM \"DistrictBanners\" b WHERE b.\"Id\" = t.\"BannerId\" AND b.\"IsActive\" = false)";

    public static IReadOnlyList<RetentionRuleDefinition> Definitions { get; } =
    [
        new("Sessions", RetentionGroups.Sessions, "Sessions", "Id", SessionEligible, "t.\"ExpiresAt\"", 90),
        new("AdminSessions", RetentionGroups.Sessions, "AdminSessions", "Id", SessionEligible, "t.\"ExpiresAt\"", 90),
        new("StayJobs", RetentionGroups.Sessions, "StayJobs", "Id", StayJobEligible, "COALESCE(t.\"ClosedAt\", t.\"UpdatedAt\")", 90),
        new("BannerDismissals", RetentionGroups.Sessions, "BannerDismissals", "UserId", BannerDismissalEligible, "t.\"DismissedAt\"", 90,
            DefaultEnabled: false, KeyColumns: ["UserId", "BannerId"]),
    ];

    public IEnumerable<IRetentionRule> Create() => Definitions.Select(d => new DeclarativeRetentionRule(d));
}
