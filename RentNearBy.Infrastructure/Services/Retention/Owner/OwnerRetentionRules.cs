using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Infrastructure.Services.Retention.Owner;

// Contract 5.1 nightly clean-up of the owner tables (group D, 04:10 to 04:30 IST). Declarative: the runner deletes in batches
// ordered by the end date (its index), FOR UPDATE SKIP LOCKED, every batch its own statement, so a stopped run resumes the next
// night. Money notes go with their calendar row. No notification.
public sealed class OwnerRetentionRules : IRetentionRuleSource
{
    public static IReadOnlyList<RetentionRuleDefinition> Definitions { get; } =
    [
        new("OwnerCalendarEntries", RetentionGroups.OwnerCalendar, "CalendarEntries", "Id",
            """t."DateTo" < @cutoffDate""", "DateTo", OwnerLimits.CalendarDays),
        new("StayBlockedRanges", RetentionGroups.OwnerCalendar, "StayBlockedRanges", "Id",
            """t."DateTo" < @cutoffDate""", "DateTo", OwnerLimits.BlockedDays),
        new("StaySpecialPrices", RetentionGroups.OwnerCalendar, "StaySpecialPrices", "Id",
            """t."DateTo" < @cutoffDate""", "DateTo", OwnerLimits.SpecialDays),
        new("FestivalReminderSent", RetentionGroups.OwnerCalendar, "FestivalReminderSent", "UserId",
            """t."FestivalDateTo" < @cutoffDate""", "FestivalDateTo", OwnerLimits.FestivalMarkDays, KeyColumns: ["UserId", "FestivalDateId"]),
        new("OwnerReminderRuns", RetentionGroups.OwnerCalendar, "OwnerReminderRuns", "RunDate",
            """t."RunDate" < @cutoffDate""", "RunDate", OwnerLimits.ReminderRunDays, KeyColumns: ["RunDate", "ListingKind"]),
        new("OwnerWeeklyStats", RetentionGroups.OwnerCalendar, "OwnerWeeklyStats", "ListingKind",
            """t."Day" < @cutoffDate""", "Day", OwnerLimits.StatsKeepDays, KeyColumns: ["ListingKind", "ListingId", "Day"]),
        new("TaxiDayOffs", RetentionGroups.OwnerCalendar, "TaxiDayOffs", "TaxiListingId",
            """t."Date" < @cutoffDate""", "Date", OwnerLimits.TaxiDayOffKeepDays, GuardExempt: true, KeyColumns: ["TaxiListingId", "Date"]),
    ];

    public IEnumerable<IRetentionRule> Create() => Definitions.Select(d => new DeclarativeRetentionRule(d));
}
