using System.Numerics;

namespace RentNearBy.Core.Models;

public enum LedgerPaymentState
{
    Open,
    SaysPaid,
    Received,
    Withdrawn,
    NotReceived,
}

public sealed record SplitShare(Guid UserId, int Share);

public sealed record ExpenseSplit(IReadOnlyList<SplitShare> Shares, int Each, int Leftover, Guid? LeftoverUserId);

public sealed record CustomSplitCheck(IReadOnlyList<SplitShare> Shares, long Assigned, long Difference, bool IsValid);

public sealed record LedgerExpense(Guid PaidByUserId, int Amount, IReadOnlyList<SplitShare> Shares);

public sealed record LedgerPayment(Guid Id, Guid FromUserId, Guid ToUserId, int Amount, LedgerPaymentState State);

public sealed record PersonBalance(Guid UserId, long Paid, long Share, long Sent, long Got)
{
    public long ExpenseNet => Paid - Share;
    public long SettleNet => ExpenseNet + Sent - Got;
}

public sealed record OpenPayment(Guid FromUserId, Guid ToUserId, long Amount);

public sealed record SettleResult(IReadOnlyList<OpenPayment> Payments, bool Exact);

public sealed record WhoPaysRow(Guid? PaymentId, Guid FromUserId, Guid ToUserId, long Amount, LedgerPaymentState State);

public sealed record WhoPaysList(IReadOnlyList<WhoPaysRow> Rows, int Done, int Total);

public sealed record BalanceSheet(
    IReadOnlyList<PersonBalance> People,
    IReadOnlyList<OpenPayment> Open,
    bool Exact,
    bool AllSettled,
    WhoPaysList WhoPaysWhom);

// Whole rupees only. Every list a caller passes as "canonical" is: owner, then JoinedAt, then UserId.
public static class TripExpenseEngine
{
    public const int MinAmount = 1;
    public const int MaxAmount = 500_000;
    public const int MaxSharers = 20;
    public const int ExactSettleMaxPeople = 14;

    public static bool Counts(LedgerPaymentState state) =>
        state is LedgerPaymentState.SaysPaid or LedgerPaymentState.Received;

    public static int CompareIds(Guid a, Guid b) =>
        string.CompareOrdinal(a.ToString("D"), b.ToString("D"));

    public static List<Guid> CanonicalOrder(Guid ownerId, IEnumerable<(Guid UserId, DateTime JoinedAt)> members)
    {
        var rest = members.Where(m => m.UserId != ownerId).ToList();
        rest.Sort((x, y) =>
        {
            var c = x.JoinedAt.CompareTo(y.JoinedAt);
            return c != 0 ? c : CompareIds(x.UserId, y.UserId);
        });
        var order = new List<Guid>(rest.Count + 1) { ownerId };
        foreach (var m in rest)
            if (!order.Contains(m.UserId)) order.Add(m.UserId);
        return order;
    }

    // Distinct ids: those in the canonical list first in its order, the others after them by id.
    public static List<Guid> Order(IReadOnlyList<Guid> canonical, IEnumerable<Guid> ids)
    {
        var index = IndexOf(canonical);
        var seen = new HashSet<Guid>();
        var known = new List<Guid>();
        var unknown = new List<Guid>();
        foreach (var id in ids)
        {
            if (!seen.Add(id)) continue;
            (index.ContainsKey(id) ? known : unknown).Add(id);
        }
        known.Sort((x, y) => index[x].CompareTo(index[y]));
        unknown.Sort(CompareIds);
        known.AddRange(unknown);
        return known;
    }

    public static ExpenseSplit EqualSplit(int amount, IReadOnlyList<Guid> canonical, IReadOnlyList<Guid> sharers, Guid payer)
    {
        if (amount < MinAmount) throw new ArgumentException("Amount must be at least 1.", nameof(amount));
        if (sharers.Count < 1 || sharers.Count > MaxSharers) throw new ArgumentException("1 to 20 sharers.", nameof(sharers));
        var s = Order(canonical, sharers);
        if (s.Count != sharers.Count) throw new ArgumentException("A sharer is listed twice.", nameof(sharers));

        var k = s.Count;
        var each = amount / k;
        var leftover = amount % k;
        Guid? leftoverUser = leftover > 0 ? (s.Contains(payer) ? payer : s[0]) : null;

        var shares = new List<SplitShare>(k);
        foreach (var id in s)
        {
            var share = each + (id == leftoverUser ? leftover : 0);
            if (share > 0) shares.Add(new SplitShare(id, share));
        }
        return new ExpenseSplit(shares, each, leftover, leftoverUser);
    }

    // Difference > 0: left to assign. Difference < 0: more than the amount. Zero entries are dropped.
    public static CustomSplitCheck CustomSplit(int amount, IReadOnlyList<Guid> canonical, IReadOnlyList<SplitShare> entries)
    {
        var by = new Dictionary<Guid, int>(entries.Count);
        long assigned = 0;
        foreach (var e in entries)
        {
            if (e.Share < 0) throw new ArgumentException("A share cannot be negative.", nameof(entries));
            if (!by.TryAdd(e.UserId, e.Share)) throw new ArgumentException("A person is listed twice.", nameof(entries));
            assigned += e.Share;
        }
        var shares = Order(canonical, by.Keys).Where(id => by[id] > 0).Select(id => new SplitShare(id, by[id])).ToList();
        var valid = amount >= MinAmount && assigned == amount && shares.Count >= 1 && shares.Count <= MaxSharers;
        return new CustomSplitCheck(shares, assigned, amount - assigned, valid);
    }

    // Late joiner (4.7): old sharers + joiner, equal split, leftover to the payer.
    public static ExpenseSplit ShareWith(int amount, IReadOnlyList<Guid> canonical, IReadOnlyList<SplitShare> stored, Guid payer, Guid joiner)
    {
        var ids = new List<Guid>(stored.Count + 1);
        foreach (var s in stored)
        {
            if (s.UserId == joiner) throw new ArgumentException("Already shares this expense.", nameof(joiner));
            ids.Add(s.UserId);
        }
        ids.Add(joiner);
        return EqualSplit(amount, canonical, ids, payer);
    }

    // Live expenses only (no deleted rows). Every canonical person gets a row, also with all zeros.
    public static List<PersonBalance> Balances(
        IReadOnlyList<Guid> canonical,
        IReadOnlyList<LedgerExpense> expenses,
        IReadOnlyList<LedgerPayment> payments)
    {
        var ids = new List<Guid>(canonical);
        foreach (var e in expenses)
        {
            ids.Add(e.PaidByUserId);
            foreach (var s in e.Shares) ids.Add(s.UserId);
        }
        foreach (var p in payments)
        {
            ids.Add(p.FromUserId);
            ids.Add(p.ToUserId);
        }
        var people = Order(canonical, ids);
        var at = IndexOf(people);
        var paid = new long[people.Count];
        var share = new long[people.Count];
        var sent = new long[people.Count];
        var got = new long[people.Count];

        foreach (var e in expenses)
        {
            long sum = 0;
            foreach (var s in e.Shares)
            {
                share[at[s.UserId]] += s.Share;
                sum += s.Share;
            }
            if (sum != e.Amount) throw new ArgumentException("Shares do not add up to the amount.", nameof(expenses));
            paid[at[e.PaidByUserId]] += e.Amount;
        }
        foreach (var p in payments)
        {
            if (!Counts(p.State)) continue;
            sent[at[p.FromUserId]] += p.Amount;
            got[at[p.ToUserId]] += p.Amount;
        }

        var rows = new List<PersonBalance>(people.Count);
        for (var i = 0; i < people.Count; i++)
            rows.Add(new PersonBalance(people[i], paid[i], share[i], sent[i], got[i]));
        return rows;
    }

    // Nets in canonical order. Exact (fewest payments) up to 14 people with a non-zero net, plain greedy above.
    public static SettleResult Settle(IReadOnlyList<(Guid UserId, long Net)> nets)
    {
        var ids = new List<Guid>(nets.Count);
        var net = new List<long>(nets.Count);
        long total = 0;
        foreach (var (userId, value) in nets)
        {
            total += value;
            if (value == 0) continue;
            ids.Add(userId);
            net.Add(value);
        }
        if (total != 0) throw new ArgumentException("Nets must sum to zero.", nameof(nets));

        var n = net.Count;
        var left = net.ToArray();
        var found = new List<(int From, int To, long Amount)>(Math.Max(0, n - 1));
        var exact = n <= ExactSettleMaxPeople;

        if (!exact)
        {
            Greedy(Enumerable.Range(0, n).ToArray(), left, found);
        }
        else if (n > 0)
        {
            var full = (1 << n) - 1;
            var sum = new long[full + 1];
            var dp = new byte[full + 1];
            for (var mask = 1; mask <= full; mask++)
            {
                sum[mask] = sum[mask & (mask - 1)] + left[BitOperations.TrailingZeroCount(mask)];
                byte best = 0;
                for (var bits = mask; bits != 0; bits &= bits - 1)
                {
                    var prev = dp[mask ^ (bits & -bits)];
                    if (prev > best) best = prev;
                }
                dp[mask] = (byte)(best + (sum[mask] == 0 ? 1 : 0));
            }

            var remaining = full;
            while (remaining != 0)
            {
                var low = remaining & -remaining;
                var rest = remaining ^ low;
                var want = dp[remaining] - 1;
                int group = 0, groupSize = int.MaxValue;
                for (var sub = rest; ; sub = (sub - 1) & rest)
                {
                    var g = sub | low;
                    if (sum[g] == 0 && dp[remaining ^ g] == want)
                    {
                        var size = BitOperations.PopCount((uint)g);
                        if (size < groupSize || (size == groupSize && g < group))
                        {
                            group = g;
                            groupSize = size;
                        }
                    }
                    if (sub == 0) break;
                }
                var members = new int[groupSize];
                var m = 0;
                for (var bits = group; bits != 0; bits &= bits - 1)
                    members[m++] = BitOperations.TrailingZeroCount(bits);
                Greedy(members, left, found);
                remaining ^= group;
            }
        }

        found.Sort((x, y) => x.From != y.From ? x.From.CompareTo(y.From) : x.To.CompareTo(y.To));
        var payments = new List<OpenPayment>(found.Count);
        foreach (var (from, to, amount) in found)
            payments.Add(new OpenPayment(ids[from], ids[to], amount));
        return new SettleResult(payments, exact);
    }

    // Stored rows that count (SaysPaid, Received) + open rows; payer order, then receiver order, stored before open.
    // Pass the stored payments oldest first.
    public static WhoPaysList WhoPaysWhom(
        IReadOnlyList<Guid> canonical,
        IReadOnlyList<LedgerPayment> payments,
        IReadOnlyList<OpenPayment> open)
    {
        var ids = new List<Guid>(canonical);
        foreach (var p in payments)
        {
            ids.Add(p.FromUserId);
            ids.Add(p.ToUserId);
        }
        foreach (var o in open)
        {
            ids.Add(o.FromUserId);
            ids.Add(o.ToUserId);
        }
        var at = IndexOf(Order(canonical, ids));

        var keyed = new List<(int From, int To, int Kind, int Seq, WhoPaysRow Row)>(payments.Count + open.Count);
        var done = 0;
        for (var i = 0; i < payments.Count; i++)
        {
            var p = payments[i];
            if (!Counts(p.State)) continue;
            if (p.State == LedgerPaymentState.Received) done++;
            keyed.Add((at[p.FromUserId], at[p.ToUserId], 0, i, new WhoPaysRow(p.Id, p.FromUserId, p.ToUserId, p.Amount, p.State)));
        }
        for (var i = 0; i < open.Count; i++)
        {
            var o = open[i];
            keyed.Add((at[o.FromUserId], at[o.ToUserId], 1, i, new WhoPaysRow(null, o.FromUserId, o.ToUserId, o.Amount, LedgerPaymentState.Open)));
        }
        keyed.Sort((x, y) =>
        {
            if (x.From != y.From) return x.From.CompareTo(y.From);
            if (x.To != y.To) return x.To.CompareTo(y.To);
            if (x.Kind != y.Kind) return x.Kind.CompareTo(y.Kind);
            return x.Seq.CompareTo(y.Seq);
        });
        return new WhoPaysList(keyed.Select(k => k.Row).ToList(), done, keyed.Count);
    }

    public static BalanceSheet Sheet(
        IReadOnlyList<Guid> canonical,
        IReadOnlyList<LedgerExpense> expenses,
        IReadOnlyList<LedgerPayment> payments)
    {
        var people = Balances(canonical, expenses, payments);
        var nets = new List<(Guid, long)>(people.Count);
        var square = true;
        foreach (var p in people)
        {
            nets.Add((p.UserId, p.SettleNet));
            if (p.SettleNet != 0) square = false;
        }
        var settle = Settle(nets);
        var allSettled = square && !payments.Any(p => p.State == LedgerPaymentState.SaysPaid);
        return new BalanceSheet(people, settle.Payments, settle.Exact, allSettled, WhoPaysWhom(canonical, payments, settle.Payments));
    }

    // What "from" still has to pay "to" right now (0 when there is no open row for the pair).
    public static long OpenAmount(IReadOnlyList<OpenPayment> open, Guid from, Guid to)
    {
        foreach (var o in open)
            if (o.FromUserId == from && o.ToUserId == to) return o.Amount;
        return 0;
    }

    private static Dictionary<Guid, int> IndexOf(IReadOnlyList<Guid> ids)
    {
        var index = new Dictionary<Guid, int>(ids.Count);
        for (var i = 0; i < ids.Count; i++) index.TryAdd(ids[i], i);
        return index;
    }

    private static void Greedy(int[] members, long[] left, List<(int From, int To, long Amount)> found)
    {
        while (true)
        {
            int debtor = -1, creditor = -1;
            foreach (var i in members)
            {
                if (left[i] < 0 && (debtor < 0 || left[i] < left[debtor])) debtor = i;
                if (left[i] > 0 && (creditor < 0 || left[i] > left[creditor])) creditor = i;
            }
            if (debtor < 0 || creditor < 0) return;
            var amount = Math.Min(-left[debtor], left[creditor]);
            left[debtor] += amount;
            left[creditor] -= amount;
            found.Add((debtor, creditor, amount));
        }
    }
}
