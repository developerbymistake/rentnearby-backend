using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Social;

namespace RentNearBy.Infrastructure.Services.Retention.Social;

// Bakhli social (contract 1.6, 1.7, 3.7): ended join requests and message snapshots after 90 days; direct rooms with no
// message for 90 days, declined rooms after 30 days (members and messages go by cascade).
public sealed class SocialRetentionRules : IRetentionRuleSource
{
    public static IReadOnlyList<RetentionRuleDefinition> Definitions { get; } =
    [
        new("TripJoinRequests", RetentionGroups.TripHousekeeping, "TripJoinRequests", "Id",
            """t."EndedAt" IS NOT NULL AND t."EndedAt" < @cutoff""",
            """t."EndedAt" """, (int)SocialLimits.JoinRequestsKeep.TotalDays),
        new("MessageSnapshots", RetentionGroups.Chat, "MessageSnapshots", "Id",
            """t."PurgeAt" < @now""",
            """t."PurgeAt" """, (int)SocialLimits.SnapshotKeep.TotalDays),
        new("DirectChatRooms", RetentionGroups.Chat, "ChatRooms", "Id",
            $"""t."Kind" = '{ChatKinds.Direct}' AND ((t."Status" = '{ChatRoomStatuses.Declined}' AND t."DeclinedAt" < @now - interval '{(int)SocialLimits.DeclinedRoomKeep.TotalDays} days') OR COALESCE(t."LastMessageAt", t."CreatedAt") < @cutoff)""",
            """t."CreatedAt" """, (int)SocialLimits.DirectRoomIdleKeep.TotalDays),
    ];

    public IEnumerable<IRetentionRule> Create() => Definitions.Select(d => new DeclarativeRetentionRule(d));
}
