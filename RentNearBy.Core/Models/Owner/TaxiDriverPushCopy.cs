using RentNearBy.Core.Interfaces.Owner;
using static RentNearBy.Core.Models.Owner.OwnerPushText;

namespace RentNearBy.Core.Models.Owner;

// Taxi Driver push copy (doc section 7, contract 5.3). Text lives only in the transient outbox row; never an amount or a fare.
public static class TaxiDriverPushCopy
{
    public static OwnerPush Festival(Guid userId, Guid festivalId, string name, DateOnly from)
        => new(userId, NotificationTypes.FestivalReminder, $"festival:{festivalId}:{userId}",
            Cut($"{WeekName(name)} starts {Day(from)}"), "Go online and check your fares.",
            UserNotificationRoutes.TaxiRoutesAndFares, Args(("section", "routes")), $"owner_festival_{festivalId}");

    public static OwnerPush PlansStarted(Guid userId, int routes, DateOnly todayIst, bool plansStartedToday = true)
        => new(userId, NotificationTypes.PlanLimitsChanged, $"planlimits:{userId}:{Ymd(todayIst)}",
            OwnerPushText.LimitsTitle(plansStartedToday), Cut($"{Count(routes, "route is", "routes are")} hidden on Free. Nothing is deleted."),
            UserNotificationRoutes.TaxiPlanVisible, Args(), $"owner_planlimits_{userId}");

    // "3 trips today" / label + " (all day)" for app bookings, or the time "9:00 am" for a trip he added.
    public static OwnerPush MorningTrips(Guid userId, Guid listingId, DateOnly todayIst, IReadOnlyList<OwnerReminderLine> lines)
        => new(userId, NotificationTypes.OwnerMorningReminder, $"ownermorning:{ListingKinds.Taxi}:{listingId}:{Ymd(todayIst)}",
            $"{Count(lines.Count, "trip", "trips")} today",
            JoinFitting(lines.OrderBy(l => l.TimeOfDay ?? TimeOnly.MinValue).Select(Line)),
            UserNotificationRoutes.TaxiBookings, Args(("date", todayIst.ToString("yyyy-MM-dd"))), $"owner_morning_{listingId}",
            NoonIstUtc(todayIst));

    public static OwnerPush PlansStarting(Guid userId, DateOnly announceDate, int freeRoutes, int trialDays)
        => new(userId, NotificationTypes.PlansStarting, $"plansstarting:{userId}:{Ymd(announceDate)}",
            $"Plans start on {Day(announceDate)}",
            Cut($"Free keeps {Count(freeRoutes, "route", "routes")}."
                + (trialDays > 0 ? $" Try Plus free for {Count(trialDays, "day", "days")}." : string.Empty)),
            UserNotificationRoutes.Plans, Args(), $"owner_plansstarting_{userId}");

    public static string WeekName(string name)
    {
        var n = name.Trim();
        return n.EndsWith(" week", StringComparison.OrdinalIgnoreCase) ? n : n + " week";
    }

    private static string Line(OwnerReminderLine l)
    {
        var label = string.IsNullOrWhiteSpace(l.Label) ? "Trip" : l.Label.Trim();
        if (l.Kind == CalendarEntryKinds.Auto || l.TimeOfDay is null) return $"{label} (all day)";
        return $"{label} {Time(l.TimeOfDay.Value)}";
    }
}
