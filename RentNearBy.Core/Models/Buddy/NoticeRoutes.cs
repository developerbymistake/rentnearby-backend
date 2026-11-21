namespace RentNearBy.Core.Models.Buddy;

public sealed record NoticeRoute(string Route, IReadOnlyDictionary<string, string> Args);

// Contract 4.1: kind -> app route + args (computed on read, never stored).
public static class NoticeRoutes
{
    public static NoticeRoute For(string kind, Guid tripId, Guid? refId)
    {
        var trip = tripId.ToString();
        return kind switch
        {
            NoticeKinds.BuddyRequest => new(UserNotificationRoutes.Buddies, Args(("tab", "requests"))),
            NoticeKinds.BuddyAccepted or NoticeKinds.NewBuddy => new(UserNotificationRoutes.Buddies, Args(("tab", "buddies"))),
            NoticeKinds.TripJoined => new(UserNotificationRoutes.TripTravellers, Args(("tripId", trip))),
            NoticeKinds.PlanUpdated => new(UserNotificationRoutes.TripView, Args(("tripId", trip))),
            NoticeKinds.TripInvite => new(UserNotificationRoutes.TripInvite, Args(("memberId", refId?.ToString() ?? string.Empty))),
            NoticeKinds.PaymentSaidPaid => new(UserNotificationRoutes.TripExpenses, Args(("tripId", trip), ("tab", "balances"))),
            NoticeKinds.SettleReminder or NoticeKinds.PaymentNotReceived or NoticeKinds.ExpensesChanged
                or NoticeKinds.ExpenseAdded or NoticeKinds.ShareChanged
                => new(UserNotificationRoutes.TripExpenses, Args(("tripId", trip))),
            NoticeKinds.TripRemoved => new(UserNotificationRoutes.TripView, Args(("tripId", trip))),
            NoticeKinds.TripDeleted => new(UserNotificationRoutes.MyTrips, Args(("deletedTripId", trip))),
            _ => new(UserNotificationRoutes.Notifications, Args()),
        };
    }

    private static IReadOnlyDictionary<string, string> Args(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }
}
