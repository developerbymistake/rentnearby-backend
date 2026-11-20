using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RentNearBy.Core.Models.Buddy;

// Contract 4.3: every push of the feature. Text lives only in the transient outbox row; nothing here touches storage.
public static class BuddyPushCopy
{
    public const int MaxKeyLength = 120;

    public static string Rupees(long amount)
    {
        var negative = amount < 0;
        var digits = Math.Abs(amount).ToString(CultureInfo.InvariantCulture);
        string grouped;
        if (digits.Length <= 3)
        {
            grouped = digits;
        }
        else
        {
            var head = digits[..^3];
            var sb = new StringBuilder();
            for (var i = 0; i < head.Length; i++)
            {
                if (i > 0 && (head.Length - i) % 2 == 0) sb.Append(',');
                sb.Append(head[i]);
            }
            grouped = sb + "," + digits[^3..];
        }
        return (negative ? "-₹" : "₹") + grouped;
    }

    public static long Bucket(DateTime nowUtc, int minutes)
        => (long)Math.Floor(new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds() / 60.0 / minutes);

    private static string N(Guid id) => id.ToString("N");

    private static string Args(params (string Key, string Value)[] pairs)
        => JsonSerializer.Serialize(pairs.ToDictionary(p => p.Key, p => p.Value));

    private static string TripArgs(Guid tripId) => Args(("tripId", tripId.ToString()));

    private static string TripBalancesArgs(Guid tripId) => Args(("tripId", tripId.ToString()), ("tab", "balances"));

    public static PushSpec BuddyRequestReceived(Guid toUserId, Guid requestId, string fromName, string fromUsername) => new(
        toUserId, NotificationTypes.BuddyRequestReceived, $"buddyreq:{N(requestId)}",
        "New buddy request", $"{fromName} ({fromUsername}) wants to be your buddy.",
        UserNotificationRoutes.Buddies, Args(("tab", "requests")), $"buddyreq:{N(toUserId)}");

    public static PushSpec BuddyRequestAccepted(Guid senderUserId, Guid requestId, string accepterName) => new(
        senderUserId, NotificationTypes.BuddyRequestAccepted, $"buddyacc:{N(requestId)}",
        "Request accepted", $"{accepterName} is now your buddy.",
        UserNotificationRoutes.Buddies, Args(("tab", "buddies")), $"buddyacc:{N(senderUserId)}");

    public static PushSpec TripInvite(Guid toUserId, Guid memberId, int memberVersion, Guid tripId, string ownerName, string title) => new(
        toUserId, NotificationTypes.TripInvite, $"tripinv:{N(memberId)}:{memberVersion}",
        "Trip invite", $"{ownerName} invited you to {title}.",
        UserNotificationRoutes.TripInvite, Args(("memberId", memberId.ToString())), $"tripinv:{N(tripId)}");

    public static PushSpec TripMemberJoined(Guid ownerUserId, Guid memberId, int memberVersion, Guid tripId, string name, string title, int travellerCount) => new(
        ownerUserId, NotificationTypes.TripMemberJoined, $"tripjoin:{N(memberId)}:{memberVersion}",
        $"{name} joined your trip", $"{title} now has {travellerCount} travellers.",
        UserNotificationRoutes.TripTravellers, TripArgs(tripId), $"tripjoin:{N(tripId)}");

    public static PushSpec TripPlanUpdated(Guid viewerUserId, Guid tripId, string ownerName, string title, DateTime nowUtc) => new(
        viewerUserId, NotificationTypes.TripPlanUpdated, $"planupd:{N(tripId)}:{N(viewerUserId)}:{Bucket(nowUtc, BuddyLimits.PlanPushBucketMinutes)}",
        "Plan updated", $"{ownerName} changed the plan of {title}.",
        UserNotificationRoutes.TripView, TripArgs(tripId), $"planupd:{N(tripId)}", BuddyLimits.PlanPushDelay);

    public static PushSpec TripMemberRemoved(Guid userId, Guid memberId, int memberVersion, Guid tripId, string ownerName, string title) => new(
        userId, NotificationTypes.TripMemberRemoved, $"triprem:{N(memberId)}:{memberVersion}",
        "Removed from a trip", $"{ownerName} removed you from {title}.",
        UserNotificationRoutes.TripView, TripArgs(tripId), $"trip:{N(tripId)}");

    // ownerName null = the owner's account was deleted.
    public static PushSpec TripDeletedForViewer(Guid userId, Guid tripId, string? ownerName, string title) => new(
        userId, NotificationTypes.TripDeletedForViewer, $"tripdel:{N(tripId)}:{N(userId)}",
        "Trip deleted", ownerName is null ? $"{title} was deleted." : $"{ownerName} deleted {title}.",
        UserNotificationRoutes.MyTrips, Args(("deletedTripId", tripId.ToString())), $"trip:{N(tripId)}");

    public static PushSpec TripExpensesUpdated(Guid userId, Guid tripId, string ownerName, string title, DateTime nowUtc) => new(
        userId, NotificationTypes.TripExpensesUpdated, $"exp:{N(tripId)}:{N(userId)}:{Bucket(nowUtc, BuddyLimits.ExpensePushBucketMinutes)}",
        "Expenses updated", $"{ownerName} updated the expenses of {title}.",
        UserNotificationRoutes.TripExpenses, TripArgs(tripId), $"exp:{N(tripId)}", BuddyLimits.ExpensePushDelay);

    private static string ReminderBody(IReadOnlyList<(string ReceiverName, long Amount)> owed, string title)
    {
        if (owed.Count == 1) return $"You owe {owed[0].ReceiverName} {Rupees(owed[0].Amount)}.";
        return $"You owe {Rupees(owed.Sum(o => o.Amount))} on {title}.";
    }

    public static PushSpec ManualReminder(Guid payerUserId, Guid tripId, string title, IReadOnlyList<(string ReceiverName, long Amount)> owed, DateOnly istDate) => new(
        payerUserId, NotificationTypes.TripSettleReminder, $"remind:{N(tripId)}:{N(payerUserId)}:{istDate:yyyyMMdd}",
        "Time to settle up", ReminderBody(owed, title),
        UserNotificationRoutes.TripExpenses, TripArgs(tripId), $"settle:{N(tripId)}");

    public static PushSpec AutoReminder(Guid payerUserId, Guid tripId, string title, IReadOnlyList<(string ReceiverName, long Amount)> owed, int daysAfterEnd) => new(
        payerUserId, NotificationTypes.TripSettleReminder, $"autoremind:{N(tripId)}:{N(payerUserId)}:{daysAfterEnd}",
        "Time to settle up", ReminderBody(owed, title),
        UserNotificationRoutes.TripExpenses, TripArgs(tripId), $"settle:{N(tripId)}");

    public static PushSpec PaymentSaidPaid(Guid userId, Guid paymentId, Guid tripId, string payerName, long amount, string title, string ownerName, bool toOwner) => new(
        userId, NotificationTypes.TripPaymentSaidPaid, $"paid:{N(paymentId)}:{N(userId)}",
        $"{payerName} says paid",
        toOwner
            ? $"{Rupees(amount)} for {title}. Mark it received when it is in your account."
            : $"{Rupees(amount)} for {title}. {ownerName} will mark it as received.",
        UserNotificationRoutes.TripExpenses, TripBalancesArgs(tripId), $"pay:{N(tripId)}");

    public static PushSpec PaymentReceived(Guid userId, Guid paymentId, Guid tripId, string payerName, string receiverName, long amount, bool toPayer) => new(
        userId, NotificationTypes.TripPaymentReceived, $"recv:{N(paymentId)}:{N(userId)}",
        "Payment received",
        toPayer
            ? $"{receiverName} got your {Rupees(amount)}."
            : $"{payerName}'s {Rupees(amount)} to you is marked as received.",
        UserNotificationRoutes.TripExpenses, TripArgs(tripId), $"pay:{N(tripId)}");

    public static PushSpec PaymentWithdrawn(Guid userId, Guid paymentId, Guid tripId, string payerName, long amount, string title) => new(
        userId, NotificationTypes.TripPaymentWithdrawn, $"wd:{N(paymentId)}:{N(userId)}",
        $"{payerName} took back a payment", $"{Rupees(amount)} for {title} is open again.",
        UserNotificationRoutes.TripExpenses, TripBalancesArgs(tripId), $"pay:{N(tripId)}");

    public static PushSpec PaymentNotReceived(Guid payerUserId, Guid paymentId, Guid tripId, string ownerName, long amount) => new(
        payerUserId, NotificationTypes.TripPaymentNotReceived, $"notrecv:{N(paymentId)}",
        "Payment not received", $"{ownerName} has not got your {Rupees(amount)} yet. Please check.",
        UserNotificationRoutes.TripExpenses, TripArgs(tripId), $"pay:{N(tripId)}");
}
