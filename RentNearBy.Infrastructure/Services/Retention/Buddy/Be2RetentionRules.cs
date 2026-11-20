namespace RentNearBy.Infrastructure.Services.Retention.Buddy;

// Lane BE2 fills this: rule "TripMemberInvites" (TripMembers Declined/Cancelled/Expired, 90 days, group B).
public sealed class Be2RetentionRules : IRetentionRuleSource
{
    public IEnumerable<IRetentionRule> Create() => [];
}
