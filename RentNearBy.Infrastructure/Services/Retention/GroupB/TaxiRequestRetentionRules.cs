using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Retention.GroupB;

// Taxi request housekeeping, in the trip enquiries slot. DueActions run first. TaxiBookings and TaxiRatings are never named here.
// DueActions is a rolling queue like OutboxMessages (never matches Pending or Running), so it is guard exempt.
public sealed class TaxiRequestRetentionRules : IRetentionRuleSource
{
    public const int DueActionDays = 14;
    public const int ParkedDueActionDays = 90;
    public const int RequestDays = 90;

    // Every final state except Failed uses @cutoff (14 days by default). Failed is a defect to look at, so it waits at least 90 days.
    public static readonly IReadOnlyList<string> QuickPurgeDueActionStates =
        DueActionStates.Final.Where(s => s != DueActionStates.Failed).ToList();

    public static readonly string DueActionsEligible =
        $"""
        (t."State" IN ({SqlList(QuickPurgeDueActionStates)}) AND t."DoneAt" < @cutoff)
        OR (t."State" = '{DueActionStates.Failed}' AND t."DoneAt" < @cutoff AND t."DoneAt" < @now - interval '{ParkedDueActionDays} days')
        """;

    // Only finished requests. Offers cascade through their foreign key.
    public static readonly string TaxiRequestsEligible =
        $"""t."Status" IN ({SqlList(RequestStates.Final)}) AND t."ClosedAt" < @cutoff""";

    public static IReadOnlyList<RetentionRuleDefinition> Definitions { get; } =
    [
        new("DueActions", RetentionGroups.TripEnquiries, "DueActions", "Id",
            DueActionsEligible, """t."DoneAt" """, DueActionDays, GuardExempt: true),

        new("TaxiRequests", RetentionGroups.TripEnquiries, "TaxiRequests", "Id",
            TaxiRequestsEligible, """t."ClosedAt" """, RequestDays),
    ];

    private static string SqlList(IEnumerable<string> values) => string.Join(",", values.Select(v => $"'{v}'"));

    public IEnumerable<IRetentionRule> Create() => Definitions.Select(d => new DeclarativeRetentionRule(d));
}
