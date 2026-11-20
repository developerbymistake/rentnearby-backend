namespace RentNearBy.Core.Models.Buddy;

public enum SaidPaidCheck { Ok, Replay, BalancesChanged, NothingToPay }

public enum NoRowAnswer { Replay, Conflict }

// Contract 2.3 E9 to E19, 4.1, 5.2, 5.3: pure payment rules (no database).
public static class TripPaymentRules
{
    // E9 under the trip lock. openAmount is set for BalancesChanged.
    public static (SaidPaidCheck Check, int? OpenAmount) CheckSaidPaid(TripLedgerSnapshot ledger, Guid opId, Guid payerId, Guid toUserId, int amount)
    {
        var existing = ledger.Payments.FirstOrDefault(p => p.Id == opId);
        if (existing is not null)
            return existing.FromUserId == payerId && existing.ToUserId == toUserId ? (SaidPaidCheck.Replay, null) : (SaidPaidCheck.NothingToPay, null);
        if (payerId == toUserId) return (SaidPaidCheck.NothingToPay, null);
        if (ledger.Payments.Any(p => p.State == PaymentStates.SaidPaid && p.FromUserId == payerId && p.ToUserId == toUserId))
            return (SaidPaidCheck.NothingToPay, null);
        var open = ledger.Open.FirstOrDefault(o => o.FromUserId == payerId && o.ToUserId == toUserId);
        if (open is null) return (SaidPaidCheck.NothingToPay, null);
        return open.Amount == amount ? (SaidPaidCheck.Ok, null) : (SaidPaidCheck.BalancesChanged, open.Amount);
    }

    public static bool IsValidAmount(int amount) => amount is >= BuddyLimits.MinPaymentAmount and <= BuddyLimits.MaxPaymentAmount;

    // A conditional transition updated 0 rows: the stored row already in the target state is a replay, anything else a conflict.
    public static NoRowAnswer OnNoRow(string storedState, string targetState)
        => storedState == targetState ? NoRowAnswer.Replay : NoRowAnswer.Conflict;

    // 409 PaymentStateChanged carries the row only when it is Received (final); an ended row is gone (null).
    public static bool ConflictShowsRow(string storedState) => storedState == PaymentStates.Received;

    // People whose SettleReminder notice must go: everyone known to the trip with no open row as payer.
    public static IReadOnlyList<Guid> WithoutOpenRow(TripLedgerSnapshot ledger, IEnumerable<Guid> travellerIds)
    {
        var payers = ledger.Open.Select(o => o.FromUserId).ToHashSet();
        return travellerIds.Concat(ledger.People.Select(p => p.UserId))
            .Distinct()
            .Where(id => !payers.Contains(id))
            .ToList();
    }

    // Open rows of one payer, in ledger order.
    public static IReadOnlyList<TripLedgerOpenRow> OpenRowsOf(TripLedgerSnapshot ledger, Guid payerId)
        => ledger.Open.Where(o => o.FromUserId == payerId).ToList();

    public static int OpenTotalOf(TripLedgerSnapshot ledger, Guid payerId)
        => ledger.Open.Where(o => o.FromUserId == payerId).Sum(o => o.Amount);

    // Notice amounts are int columns; a total above int range is clamped (never happens inside the limits).
    public static int ClampAmount(long amount) => (int)Math.Clamp(amount, 0, int.MaxValue);

    public static DateTime IstDayStartUtc(DateTime nowUtc) => IstTime.IstMidnightUtc(IstTime.TodayIst(nowUtc));

    // Automatic reminders: trip end + 3 and + 7 days (IST dates). null = not a reminder day.
    public static int? AutoReminderDay(DateOnly endDate, DateOnly todayIst)
    {
        var days = todayIst.DayNumber - endDate.DayNumber;
        return BuddyLimits.AutoReminderDaysAfterEnd.Contains(days) ? days : null;
    }

    public static IReadOnlyList<DateOnly> AutoReminderEndDates(DateOnly todayIst)
        => BuddyLimits.AutoReminderDaysAfterEnd.Select(d => todayIst.AddDays(-d)).ToList();

    // Who hears about each transition (the actor is never pushed about his own action).
    public static bool SaidPaidNoticeToOwner(Guid ownerId, Guid payerId) => ownerId != payerId;

    public static bool SaidPaidCopyToReceiver(Guid ownerId, Guid payerId, Guid receiverId) => receiverId != ownerId && receiverId != payerId;

    public static bool ReceivedPushToPayer(Guid ownerId, Guid payerId) => payerId != ownerId;

    public static bool ReceivedPushToReceiver(Guid ownerId, Guid payerId, Guid receiverId) => receiverId != ownerId && receiverId != payerId;

    public static bool WithdrawnPushToOwner(Guid ownerId, Guid payerId) => ownerId != payerId;

    public static bool WithdrawnPushToReceiver(Guid ownerId, Guid payerId, Guid receiverId) => receiverId != ownerId && receiverId != payerId;

    public static bool NotReceivedToPayer(Guid ownerId, Guid payerId) => payerId != ownerId;
}
