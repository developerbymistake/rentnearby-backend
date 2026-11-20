using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Infrastructure.Services.Retention.Buddy;

// Ended buddy requests, 90 days after EndedAt (cooldown needs 30, the Sent list 7). Index ix_buddyrequests_ended.
public sealed class Be1RetentionRules : IRetentionRuleSource
{
    public static IReadOnlyList<RetentionRuleDefinition> Definitions { get; } =
    [
        new("BuddyRequests", RetentionGroups.TripHousekeeping, "BuddyRequests", "Id",
            """t."EndedAt" IS NOT NULL AND t."EndedAt" < @cutoff""", """t."EndedAt" """, BuddyLimits.EndedRequestRetentionDays),
    ];

    public IEnumerable<IRetentionRule> Create() => Definitions.Select(d => new DeclarativeRetentionRule(d));
}
