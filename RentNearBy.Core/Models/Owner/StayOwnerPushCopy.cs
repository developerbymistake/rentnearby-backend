using RentNearBy.Core.Interfaces.Owner;
using static RentNearBy.Core.Models.Owner.OwnerPushText;

namespace RentNearBy.Core.Models.Owner;

// Stay Owner push copy (doc section 7, contract 5.3). Text lives only in the transient outbox row; never an amount.
public static class StayOwnerPushCopy
{
    public static OwnerPush Festival(Guid userId, Guid festivalId, string name, DateOnly from, DateOnly to, int daysAway)
        => new(userId, NotificationTypes.FestivalReminder, $"festival:{festivalId}:{userId}",
            Cut($"{name} is in {Weeks(daysAway)}"), Cut($"Set your price for {Range(from, to)}?"),
            UserNotificationRoutes.StaySpecialPrices,
            Args(("festivalId", festivalId), ("dateFrom", from.ToString("yyyy-MM-dd")), ("dateTo", to.ToString("yyyy-MM-dd"))),
            $"owner_festival_{festivalId}");

    // FB-39 (D-12): "Plans have started" only on the day the payment switch went on; any other loss (Plus ended) says so.
    // FB-40 (D-27): when the only loss is paused special prices, the words and the route lead to the Special prices list.
    public static OwnerPush PlansStarted(Guid userId, int photos, int roomTypes, DateOnly todayIst, int pausedRules = 0, bool plansStartedToday = true)
    {
        var title = OwnerPushText.LimitsTitle(plansStartedToday);
        if (photos == 0 && roomTypes == 0 && pausedRules > 0)
            return new(userId, NotificationTypes.PlanLimitsChanged, $"planlimits:{userId}:{Ymd(todayIst)}",
                title, Cut($"{Count(pausedRules, "special price is", "special prices are")} paused on Free. Nothing is deleted."),
                UserNotificationRoutes.StaySpecialPrices, Args(), $"owner_planlimits_{userId}");
        var parts = new List<string>();
        if (photos > 0) parts.Add(Count(photos, "photo", "photos"));
        if (roomTypes > 0) parts.Add(Count(roomTypes, "room type", "room types"));
        var what = parts.Count == 0 ? "Some items" : string.Join(" and ", parts);
        var verb = parts.Count == 1 && (photos == 1 || roomTypes == 1) ? "is" : "are";
        return new(userId, NotificationTypes.PlanLimitsChanged, $"planlimits:{userId}:{Ymd(todayIst)}",
            title, Cut($"{what} {verb} hidden on Free. Nothing is deleted."),
            UserNotificationRoutes.StayPlanVisible, Args(), $"owner_planlimits_{userId}");
    }

    public static OwnerPush EditApproved(Guid userId, Guid stayId, DateTime pendingEditAt, string? newName)
        => new(userId, NotificationTypes.StayEditApproved, $"stayedit:{stayId}:{pendingEditAt.Ticks}",
            "Your change is live", Cut(string.IsNullOrWhiteSpace(newName) ? "Your stay details are updated" : $"Name updated to {newName.Trim()}"),
            UserNotificationRoutes.MyStays, Args(), $"owner_stayedit_{stayId}");

    public static OwnerPush EditRejected(Guid userId, Guid stayId, DateTime pendingEditAt, string reason)
        => new(userId, NotificationTypes.StayEditRejected, $"stayedit:{stayId}:{pendingEditAt.Ticks}",
            "Change not approved", Cut(reason), UserNotificationRoutes.MyStays, Args(), $"owner_stayedit_{stayId}");

    // "2 check-ins today" / labels with the detail when it fits.
    public static OwnerPush MorningCheckIns(Guid userId, Guid listingId, DateOnly todayIst, IReadOnlyList<OwnerReminderLine> lines)
        => new(userId, NotificationTypes.OwnerMorningReminder, $"ownermorning:{ListingKinds.Stay}:{listingId}:{Ymd(todayIst)}",
            $"{Count(lines.Count, "check-in", "check-ins")} today",
            JoinFitting(lines.Select(l => string.IsNullOrWhiteSpace(l.Detail) ? Label(l) : $"{Label(l)} ({l.Detail!.Trim()})")),
            UserNotificationRoutes.StayCalendar, Args(("date", todayIst.ToString("yyyy-MM-dd"))), $"owner_morning_{listingId}",
            NoonIstUtc(todayIst));

    public static OwnerPush PlansStarting(Guid userId, DateOnly announceDate, int freePhotos, int freeRoomTypes, int trialDays)
        => new(userId, NotificationTypes.PlansStarting, $"plansstarting:{userId}:{Ymd(announceDate)}",
            $"Plans start on {Day(announceDate)}",
            Cut($"Free keeps {Count(freePhotos, "photo", "photos")} and {Count(freeRoomTypes, "room type", "room types")}."
                + (trialDays > 0 ? $" Try Plus free for {Count(trialDays, "day", "days")}." : string.Empty)),
            UserNotificationRoutes.Plans, Args(), $"owner_plansstarting_{userId}");

    private static string Label(OwnerReminderLine l) => string.IsNullOrWhiteSpace(l.Label) ? "Guest" : l.Label.Trim();
}
