using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Services.Retention;

namespace RentNearBy.Infrastructure.Services.Owner.Quote;

// ARCHITECTURE 2.4: issue rows (numbers only) are deleted 180 days after the first issue, by the IssuedAt index, in the owner slot.
// After the purge a re-download of that old document gets a new number (A-4).
public sealed class StayQuoteRetentionRules : IRetentionRuleSource
{
    public static IReadOnlyList<RetentionRuleDefinition> Definitions { get; } =
    [
        new("StayQuoteIssues", RetentionGroups.OwnerCalendar, "StayQuoteIssues", "UserId",
            """t."IssuedAt" < @cutoff""", "IssuedAt", QuoteRules.IssueRetentionDays, KeyColumns: ["UserId", "DocumentId", "Kind"]),
    ];

    public IEnumerable<IRetentionRule> Create() => Definitions.Select(d => new DeclarativeRetentionRule(d));
}
