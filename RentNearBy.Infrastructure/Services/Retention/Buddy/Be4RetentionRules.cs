using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Infrastructure.Services.Retention.Buddy;

// Contract 6: stored notices live 30 days after their last upsert (acted-on rows are deleted by the action itself);
// ended payment rows (Withdrawn, NotReceived) live 90 days. Batched DELETE ... LIMIT @n FOR UPDATE SKIP LOCKED by the runner.
public sealed class Be4RetentionRules : IRetentionRuleSource
{
    public static readonly RetentionRuleDefinition UserNotices = new(
        "UserNotices", RetentionGroups.Notifications, "UserNotices", "Id",
        "t.\"UpdatedAt\" < @cutoff", "t.\"UpdatedAt\"", BuddyLimits.NoticeRetentionDays, GuardExempt: true);

    public static readonly RetentionRuleDefinition TripPaymentsEnded = new(
        "TripPaymentsEnded", RetentionGroups.TripHousekeeping, "TripPayments", "Id",
        "t.\"EndedAt\" IS NOT NULL AND t.\"EndedAt\" < @cutoff", "t.\"EndedAt\"", BuddyLimits.EndedPaymentRetentionDays);

    public static IReadOnlyList<RetentionRuleDefinition> Definitions { get; } = [UserNotices, TripPaymentsEnded];

    public IEnumerable<IRetentionRule> Create() => Definitions.Select(d => new DeclarativeRetentionRule(d));
}
