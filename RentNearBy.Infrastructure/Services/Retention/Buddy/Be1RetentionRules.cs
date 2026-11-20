namespace RentNearBy.Infrastructure.Services.Retention.Buddy;

// Lane BE1 fills this: rule "BuddyRequests" (ended requests, 90 days, group B).
public sealed class Be1RetentionRules : IRetentionRuleSource
{
    public IEnumerable<IRetentionRule> Create() => [];
}
