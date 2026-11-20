using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Buddy.Be4;

// Contract 6: the morning reminder (09:00 IST) on trip end + 3 and + 7 days to every payer with an open row (the owner too).
// Per batch of 200 trips: trips (1) + travellers (1) + ledger (3) + one conditional notice upsert + one push insert = 7.
// A second run on the same day writes nothing new (the notice is not older than today, the push keys exist).
public sealed class SettleReminderJob(
    ApplicationDbContext db,
    ITripAccess access,
    ITripLedger ledger,
    NoticeWriter notices,
    TimeProvider clock,
    ILogger<SettleReminderJob> logger) : IBuddyFeatureJob
{
    public static readonly TimeOnly RunsAtIst = new(9, 0);

    internal const string BatchSql = """
        SELECT t."Id", t."Title", t."EndDate", t."OwnerUserId"
        FROM "Trips" t JOIN "TripExpenseState" s ON s."TripId" = t."Id"
        WHERE t."EndDate" = ANY(@dates) AND s."OpenPayments" > 0 AND t."Id" > @after
        ORDER BY t."Id"
        LIMIT @n
        """;

    public string Name => "SettleReminder";

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public TimeOnly? DailyAtIst => RunsAtIst;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = IstTime.TodayIst(now);
        var dates = TripPaymentRules.AutoReminderEndDates(today).ToArray();
        var after = Guid.Empty;
        var written = 0;
        while (true) // batch-loop
        {
            var batch = await db.Database.SqlQueryRaw<TripRow>(BatchSql,
                new NpgsqlParameter("dates", NpgsqlDbType.Array | NpgsqlDbType.Date) { Value = dates },
                new NpgsqlParameter("after", NpgsqlDbType.Uuid) { Value = after },
                new NpgsqlParameter("n", NpgsqlDbType.Integer) { Value = BuddyLimits.ReminderJobTripBatch }).ToListAsync(ct);
            if (batch.Count == 0) break;
            written += await RunBatchAsync(batch, today, now, ct);
            if (batch.Count < BuddyLimits.ReminderJobTripBatch) break;
            after = batch[^1].Id;
        }
        if (written > 0) notices.Wake();
        return written;
    }

    private async Task<int> RunBatchAsync(IReadOnlyList<TripRow> batch, DateOnly today, DateTime now, CancellationToken ct)
    {
        var ids = batch.Select(t => t.Id).ToList();
        var travellers = await access.GetTravellersManyAsync(ids, ct);
        var orders = ids.ToDictionary(id => id, id => (IReadOnlyList<Guid>)(travellers.GetValueOrDefault(id)?.Select(t => t.UserId).ToList() ?? []));
        var ledgers = await ledger.LoadManyAsync(orders, ct);
        var plan = Plan(batch, travellers, ledgers, today);
        if (plan.Count == 0) return 0;

        return await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var rows = await notices.UpsertManyIfOlderAsync(plan.Select(p => p.Notice).ToList(), TripPaymentRules.IstDayStartUtc(now), c);
            var done = rows.Select(r => (r.UserId, r.TripId)).ToHashSet();
            await notices.PushAsync(plan.Where(p => done.Contains((p.Notice.UserId, p.Notice.TripId))).Select(p => p.Push).ToList(), c);
            await tx.CommitAsync(c);
            return done.Count;
        }, ct, logger);
    }

    public sealed record Reminder(NoticeUpsert Notice, PushSpec Push);

    // Pure: one reminder per payer with open rows per trip of the batch.
    public static IReadOnlyList<Reminder> Plan(
        IReadOnlyList<TripRow> batch, IReadOnlyDictionary<Guid, IReadOnlyList<TripTraveller>> travellers,
        IReadOnlyDictionary<Guid, TripLedgerSnapshot> ledgers, DateOnly today)
    {
        var result = new List<Reminder>();
        foreach (var trip in batch)
        {
            if (trip.EndDate is not { } end || TripPaymentRules.AutoReminderDay(end, today) is not { } day) continue;
            if (!ledgers.TryGetValue(trip.Id, out var snapshot)) continue;
            var people = travellers.GetValueOrDefault(trip.Id) ?? [];
            var known = people.Select(p => p.UserId).ToHashSet();
            foreach (var payer in snapshot.Open.GroupBy(o => o.FromUserId))
            {
                if (!known.Contains(payer.Key)) continue;
                var owed = payer.Select(o => (PayeeName(people, o.ToUserId), (long)o.Amount)).ToList();
                var total = TripPaymentRules.ClampAmount(owed.Sum(o => o.Item2));
                result.Add(new Reminder(
                    new NoticeUpsert(payer.Key, NoticeKinds.SettleReminder, trip.Id, trip.OwnerUserId, null, total),
                    BuddyPushCopy.AutoReminder(payer.Key, trip.Id, trip.Title, owed, day)));
            }
        }
        return result;
    }

    private static string PayeeName(IReadOnlyList<TripTraveller> people, Guid userId)
        => people.FirstOrDefault(p => p.UserId == userId) is { } t ? MoneyPeople.PushName(t, people) : MoneyPeople.DeletedName;

    public sealed class TripRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateOnly? EndDate { get; set; }
        public Guid OwnerUserId { get; set; }
    }
}
