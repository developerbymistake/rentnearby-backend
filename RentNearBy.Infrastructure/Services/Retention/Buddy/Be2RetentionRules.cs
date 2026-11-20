using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Infrastructure.Services.Retention.Buddy;

// Lane BE2: ended invites (Declined, Cancelled, Expired) are deleted 90 days after they ended (index ix_tripmembers_ended).
// Joined, Left and Removed rows are never purged while the trip exists.
public sealed class Be2RetentionRules : IRetentionRuleSource
{
    public static IReadOnlyList<RetentionRuleDefinition> Definitions { get; } =
    [
        new("TripMemberInvites", RetentionGroups.TripHousekeeping, "TripMembers", "Id",
            $"""t."Status" IN ({TripMemberStatuses.InviteEndedSqlList}) AND t."EndedAt" < @cutoff""",
            """t."EndedAt" """, BuddyLimits.EndedInviteRetentionDays),
    ];

    public IEnumerable<IRetentionRule> Create() => Definitions.Select(d => new DeclarativeRetentionRule(d));
}
