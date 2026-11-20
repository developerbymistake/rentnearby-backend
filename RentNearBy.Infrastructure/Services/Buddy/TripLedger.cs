using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Buddy;

// The one reader of money (contract 3.4). Three statements per load, whatever the number of rows; the many-trip load is
// the same three statements over ANY(@ids).
public sealed class TripLedger(ApplicationDbContext db, ILogger<TripLedger> logger) : ITripLedger
{
    internal const string MoneySql = """
        SELECT x.t AS "TripId", x.u AS "UserId", sum(x.paid)::int AS "Paid", sum(x.share)::int AS "Share", sum(x.sent)::int AS "Sent", sum(x.got)::int AS "Got"
        FROM (
          SELECT e."TripId" AS t, e."PaidByUserId" AS u, e."Amount" AS paid, 0 AS share, 0 AS sent, 0 AS got
            FROM "TripExpenses" e WHERE e."TripId" = ANY(@trips) AND e."DeletedAt" IS NULL
          UNION ALL SELECT s."TripId", s."UserId", 0, s."Share", 0, 0 FROM "TripExpenseShares" s WHERE s."TripId" = ANY(@trips)
          UNION ALL SELECT p."TripId", p."FromUserId", 0, 0, p."Amount", 0 FROM "TripPayments" p WHERE p."TripId" = ANY(@trips) AND p."State" IN ('SaidPaid','Received')
          UNION ALL SELECT p."TripId", p."ToUserId", 0, 0, 0, p."Amount" FROM "TripPayments" p WHERE p."TripId" = ANY(@trips) AND p."State" IN ('SaidPaid','Received')
        ) x GROUP BY x.t, x.u
        """;

    internal const string PaymentsSql = """
        SELECT q."TripId", q."Id", q."FromUserId", q."ToUserId", q."Amount", q."State", q."SaidPaidAt", q."ReceivedAt", q."ReceivedByUserId", q."EndedAt"
        FROM (
          SELECT p."TripId", p."Id", p."FromUserId", p."ToUserId", p."Amount", p."State", p."SaidPaidAt", p."ReceivedAt", p."ReceivedByUserId", p."EndedAt",
                 row_number() OVER (PARTITION BY p."TripId" ORDER BY p."SaidPaidAt" DESC, p."Id" DESC) AS rn
          FROM "TripPayments" p WHERE p."TripId" = ANY(@trips) AND p."State" <> 'Withdrawn'
        ) q WHERE q.rn <= @max
        """;

    internal const string StateSql = """
        SELECT s."TripId", s."Version", s."ExpenseCount", s."TotalSpent", s."ReopenedAt", s."ReopenedExpenseId"
        FROM "TripExpenseState" s WHERE s."TripId" = ANY(@trips)
        """;

    internal const string RefreshSql = """
        INSERT INTO "TripExpenseState" AS st ("TripId", "Version", "ExpenseCount", "TotalSpent", "OpenPayments", "SaidPaidCount", "ReceivedCount", "ReopenedAt", "ReopenedExpenseId", "UpdatedAt")
        SELECT @trip, 1, c.cnt, c.total, @open, @said, @recv,
               CASE WHEN @reopen THEN now() END, CASE WHEN @reopen THEN @changed END, now()
        FROM (SELECT count(*)::int AS cnt, COALESCE(sum(e."Amount"), 0)::bigint AS total
              FROM "TripExpenses" e WHERE e."TripId" = @trip AND e."DeletedAt" IS NULL) c
        ON CONFLICT ("TripId") DO UPDATE SET
          "Version" = st."Version" + 1,
          "ExpenseCount" = EXCLUDED."ExpenseCount",
          "TotalSpent" = EXCLUDED."TotalSpent",
          "OpenPayments" = EXCLUDED."OpenPayments",
          "SaidPaidCount" = EXCLUDED."SaidPaidCount",
          "ReceivedCount" = EXCLUDED."ReceivedCount",
          "ReopenedAt" = CASE WHEN @settled THEN NULL WHEN @reopen THEN now() ELSE st."ReopenedAt" END,
          "ReopenedExpenseId" = CASE WHEN @settled THEN NULL WHEN @reopen THEN @changed ELSE st."ReopenedExpenseId" END,
          "UpdatedAt" = now()
        RETURNING st."TripId", st."Version", st."ExpenseCount", st."TotalSpent", st."ReopenedAt", st."ReopenedExpenseId"
        """;

    public async Task<TripLedgerSnapshot> LoadAsync(Guid tripId, IReadOnlyList<Guid> canonicalOrder, CancellationToken ct = default)
    {
        var many = await LoadManyAsync(new Dictionary<Guid, IReadOnlyList<Guid>> { [tripId] = canonicalOrder }, ct);
        return many[tripId];
    }

    public async Task<IReadOnlyDictionary<Guid, TripLedgerSnapshot>> LoadManyAsync(
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> canonicalOrderByTrip, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, TripLedgerSnapshot>();
        if (canonicalOrderByTrip.Count == 0) return result;
        var ids = canonicalOrderByTrip.Keys.ToArray();

        var money = await ReadMoneyAsync(ids, ct);
        var payments = await ReadPaymentsAsync(ids, ct);
        var states = (await db.Database.SqlQueryRaw<StateRow>(StateSql, Trips(ids)).ToListAsync(ct)).ToDictionary(s => s.TripId);

        foreach (var (tripId, order) in canonicalOrderByTrip)
        {
            var state = states.TryGetValue(tripId, out var s) ? s.ToFacts() : null;
            result[tripId] = Build(tripId, order, money, payments, state);
        }
        return result;
    }

    public async Task<TripLedgerSnapshot> RefreshStateAsync(
        Guid tripId, IReadOnlyList<Guid> canonicalOrder, Guid? changedExpenseId, CancellationToken ct = default)
    {
        Guid[] ids = [tripId];
        var money = await ReadMoneyAsync(ids, ct);
        var payments = await ReadPaymentsAsync(ids, ct);
        var computed = Build(tripId, canonicalOrder, money, payments, null);
        var reopen = TripLedgerMath.Reopens(changedExpenseId, computed.ReceivedCount, computed.Settled);

        var rows = await db.Database.SqlQueryRaw<StateRow>(RefreshSql,
            new NpgsqlParameter("trip", NpgsqlDbType.Uuid) { Value = tripId },
            new NpgsqlParameter("open", NpgsqlDbType.Integer) { Value = computed.OpenPayments },
            new NpgsqlParameter("said", NpgsqlDbType.Integer) { Value = computed.SaidPaidCount },
            new NpgsqlParameter("recv", NpgsqlDbType.Integer) { Value = computed.ReceivedCount },
            new NpgsqlParameter("reopen", NpgsqlDbType.Boolean) { Value = reopen },
            new NpgsqlParameter("settled", NpgsqlDbType.Boolean) { Value = computed.Settled },
            new NpgsqlParameter("changed", NpgsqlDbType.Uuid) { Value = (object?)changedExpenseId ?? DBNull.Value })
            .ToListAsync(ct);
        var facts = rows[0].ToFacts();
        return computed with
        {
            Version = facts.Version,
            ExpenseCount = facts.ExpenseCount,
            TotalSpent = facts.TotalSpent,
            ReopenedAt = facts.ReopenedAt,
            ReopenedExpenseId = facts.ReopenedExpenseId,
        };
    }

    private TripLedgerSnapshot Build(
        Guid tripId, IReadOnlyList<Guid> order, IReadOnlyList<MoneyRow> money, IReadOnlyList<PaymentRow> payments, LedgerStateFacts? state)
    {
        var people = money.Where(m => m.TripId == tripId).Select(m => new TripLedgerPerson(m.UserId, m.Paid, m.Share, m.Sent, m.Got)).ToList();
        var rows = payments.Where(p => p.TripId == tripId)
            .Select(p => new TripLedgerPaymentRow(p.Id, p.FromUserId, p.ToUserId, p.Amount, p.State, p.SaidPaidAt, p.ReceivedAt, p.ReceivedByUserId, p.EndedAt))
            .ToList();
        var built = TripLedgerMath.Build(tripId, order, people, rows, state);
        if (!built.Exact) logger.LogWarning("buddy.ledger.greedy {TripId} {People}", tripId, people.Count);
        return built.Snapshot;
    }

    private Task<List<MoneyRow>> ReadMoneyAsync(Guid[] ids, CancellationToken ct)
        => db.Database.SqlQueryRaw<MoneyRow>(MoneySql, Trips(ids)).ToListAsync(ct);

    private Task<List<PaymentRow>> ReadPaymentsAsync(Guid[] ids, CancellationToken ct)
        => db.Database.SqlQueryRaw<PaymentRow>(PaymentsSql, Trips(ids),
            new NpgsqlParameter("max", NpgsqlDbType.Integer) { Value = BuddyLimits.LedgerPaymentsMax }).ToListAsync(ct);

    private static NpgsqlParameter Trips(Guid[] ids) => new("trips", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids };

    private sealed class MoneyRow
    {
        public Guid TripId { get; set; }
        public Guid UserId { get; set; }
        public int Paid { get; set; }
        public int Share { get; set; }
        public int Sent { get; set; }
        public int Got { get; set; }
    }

    private sealed class PaymentRow
    {
        public Guid TripId { get; set; }
        public Guid Id { get; set; }
        public Guid FromUserId { get; set; }
        public Guid ToUserId { get; set; }
        public int Amount { get; set; }
        public string State { get; set; } = string.Empty;
        public DateTime SaidPaidAt { get; set; }
        public DateTime? ReceivedAt { get; set; }
        public Guid? ReceivedByUserId { get; set; }
        public DateTime? EndedAt { get; set; }
    }

    private sealed class StateRow
    {
        public Guid TripId { get; set; }
        public long Version { get; set; }
        public int ExpenseCount { get; set; }
        public long TotalSpent { get; set; }
        public DateTime? ReopenedAt { get; set; }
        public Guid? ReopenedExpenseId { get; set; }

        public LedgerStateFacts ToFacts() => new(Version, ExpenseCount, TotalSpent, ReopenedAt, ReopenedExpenseId);
    }
}
