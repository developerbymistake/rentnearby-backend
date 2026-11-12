using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services.Retention.GroupB;

// Order here is the run order; offers go before enquiries.
public sealed class GroupBRetentionRules : IRetentionRuleSource
{
    public static IReadOnlyList<RetentionRuleDefinition> Definitions { get; } =
    [
        new("OutboxMessages", RetentionGroups.TripHousekeeping, "OutboxMessages", "Id",
            """t."PublishedAt" IS NOT NULL AND t."PublishedAt" < @cutoff""",
            """t."PublishedAt" """, 3, GuardExempt: true),

        new("TripReminderRuns", RetentionGroups.TripHousekeeping, "TripReminderRuns", "RunDate",
            """t."RunDate" < @cutoffDate""",
            """t."RunDate" """, 30, GuardExempt: true),

        new("TripShortlistItems", RetentionGroups.TripHousekeeping, "TripShortlistItems", "Id",
            $"""((t."State" IN ('Closed','Booked') AND t."ClosedAt" < @cutoff) OR (t."State" = 'Sent' AND t."ClosesAt" < @cutoff)) AND {TripRetentionSql.TripNotRunning("t.\"TripId\"")}""",
            """COALESCE(t."ClosedAt", t."ClosesAt")""", 30, GuardExempt: true),

        new("TripOffers", RetentionGroups.TripEnquiries, "TripOffers", "Id",
            $"""
            t."Status" IN ('Expired','Declined','Superseded')
            AND GREATEST(t."CreatedAt", COALESCE(t."RespondedAt", t."CreatedAt")) < @cutoff
            AND EXISTS (SELECT 1 FROM "TripEnquiries" e WHERE e."Id" = t."TripEnquiryId" AND e."UpdatedAt" < @cutoff AND {TripRetentionSql.EnquiryDeletable("e")})
            """,
            """t."CreatedAt" """, 90),

        new("TripEnquiries", RetentionGroups.TripEnquiries, "TripEnquiries", "Id",
            $"""t."UpdatedAt" < @cutoff AND {TripRetentionSql.EnquiryDeletable("t")}""",
            """t."UpdatedAt" """, 90),

        new("TripEnquiryOrphans", RetentionGroups.TripEnquiries, "TripEnquiries", "Id",
            $"""t."TripId" IS NULL AND t."UpdatedAt" < @cutoff AND {TripRetentionSql.EnquiryDeletable("t")}""",
            """t."UpdatedAt" """, 180),
    ];

    public IEnumerable<IRetentionRule> Create() => Definitions.Select(d => new DeclarativeRetentionRule(d));
}
