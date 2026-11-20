namespace RentNearBy.Core.Models.Buddy;

public sealed record LedgerStateFacts(long Version, int ExpenseCount, long TotalSpent, DateTime? ReopenedAt, Guid? ReopenedExpenseId);

// Pure part of ITripLedger (contract 3.4): grouped money rows + payment rows -> snapshot, through TripExpenseEngine.Settle.
public static class TripLedgerMath
{
    public sealed record Result(TripLedgerSnapshot Snapshot, bool Exact);

    // payments: every non-Withdrawn row of the trip (NotReceived included, for AfterNotReceivedAt).
    public static Result Build(
        Guid tripId, IReadOnlyList<Guid> canonicalOrder, IReadOnlyList<TripLedgerPerson> money,
        IReadOnlyList<TripLedgerPaymentRow> payments, LedgerStateFacts? state)
    {
        var order = TripExpenseEngine.Order(canonicalOrder, money.Select(m => m.UserId));
        var byId = money.GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => g.Aggregate(
            new TripLedgerPerson(g.Key, 0, 0, 0, 0),
            (a, b) => new TripLedgerPerson(g.Key, a.Paid + b.Paid, a.Share + b.Share, a.Sent + b.Sent, a.Got + b.Got)));
        var people = order.Select(id => byId[id]).ToList();

        var settle = TripExpenseEngine.Settle(people.Select(p => (p.UserId, (long)p.SettleNet)).ToList());

        var counting = payments
            .Where(p => p.State is PaymentStates.SaidPaid or PaymentStates.Received)
            .OrderBy(p => p.SaidPaidAt).ThenBy(p => p.Id, Comparer<Guid>.Create(TripExpenseEngine.CompareIds))
            .ToList();
        var open = settle.Payments
            .Select(o => new TripLedgerOpenRow(o.FromUserId, o.ToUserId, checked((int)o.Amount), AfterNotReceivedAt(payments, o.FromUserId, o.ToUserId)))
            .ToList();
        var saidPaid = counting.Count(p => p.State == PaymentStates.SaidPaid);
        var received = counting.Count(p => p.State == PaymentStates.Received);
        var settled = open.Count == 0 && saidPaid == 0;

        var snapshot = new TripLedgerSnapshot(
            tripId, state?.Version ?? 0, state?.ExpenseCount ?? 0, state?.TotalSpent ?? 0,
            people, counting, open, settled, open.Count + saidPaid, saidPaid, received,
            state?.ReopenedAt, state?.ReopenedExpenseId);
        return new Result(snapshot, settle.Exact);
    }

    // EndedAt of the newest NotReceived row of the pair when it is newer than every other row of the pair.
    public static DateTime? AfterNotReceivedAt(IReadOnlyList<TripLedgerPaymentRow> payments, Guid from, Guid to)
    {
        var pair = payments.Where(p => p.FromUserId == from && p.ToUserId == to).ToList();
        var newestNotReceived = pair
            .Where(p => p.State == PaymentStates.NotReceived && p.EndedAt != null)
            .OrderByDescending(p => p.EndedAt)
            .FirstOrDefault();
        if (newestNotReceived is null) return null;
        var newestOther = pair
            .Where(p => p.Id != newestNotReceived.Id)
            .Select(p => p.ReceivedAt ?? p.EndedAt ?? p.SaidPaidAt)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        return newestNotReceived.EndedAt > newestOther ? newestNotReceived.EndedAt : null;
    }

    // Reopen marks after a money write (contract 3.4).
    public static bool Reopens(Guid? changedExpenseId, int receivedCount, bool settledAfter)
        => changedExpenseId != null && receivedCount > 0 && !settledAfter;
}
