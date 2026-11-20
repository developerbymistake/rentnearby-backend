namespace RentNearBy.Infrastructure.Services.Retention.Buddy;

// Lane BE4 fills this: rules "UserNotices" (30 days, group A, guard exempt) and "TripPaymentsEnded" (90 days, group B).
public sealed class Be4RetentionRules : IRetentionRuleSource
{
    public IEnumerable<IRetentionRule> Create() => [];
}
