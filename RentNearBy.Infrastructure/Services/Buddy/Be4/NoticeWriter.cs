using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Buddy.Be4;

// The notification policy, implemented once (contract 3.5, 4): UserNotices rows are upserted in place (one per user, kind,
// trip; ids and numbers only), pushes are outbox rows whose unique key coalesces and dedupes. Every method is ONE statement
// whatever the number of recipients, on the caller's context and transaction.
public sealed class NoticeWriter(ApplicationDbContext db, IOutboxSignal? signal = null) : INoticeWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, string> CollapsingData = new Dictionary<string, string>
    {
        ["collapse"] = "1",
        ["feature"] = "buddy",
    };

    private static readonly IReadOnlyDictionary<string, string> MustArriveData = new Dictionary<string, string>
    {
        ["feature"] = "buddy",
    };

    // FCM keeps only 4 collapse keys per offline device and drops the rest, so only informational pushes collapse.
    // Requests, invites, reminders and pay requests are sent without a collapse key (the Android tag still replaces on the phone).
    internal static readonly IReadOnlySet<string> CollapsingKinds = new HashSet<string>
    {
        NotificationTypes.BuddyRequestAccepted, NotificationTypes.TripMemberJoined, NotificationTypes.TripPlanUpdated,
        NotificationTypes.TripExpensesUpdated, NotificationTypes.TripPaymentReceived, NotificationTypes.TripPaymentWithdrawn,
    };

    // Rows of deleted accounts are skipped (FK), duplicates in one call keep the last.
    internal const string UpsertSql = """
        INSERT INTO "UserNotices" AS n ("Id", "UserId", "Kind", "TripId", "ActorUserId", "RefId", "Amount", "Count", "CreatedAt", "UpdatedAt")
        SELECT gen_random_uuid(), u.user_id, u.kind, u.trip_id, u.actor, u.ref, u.amount, 1, now(), now()
        FROM unnest(@users, @kinds, @trips, @actors, @refs, @amounts) AS u(user_id, kind, trip_id, actor, ref, amount)
        WHERE EXISTS (SELECT 1 FROM "Users" x WHERE x."Id" = u.user_id)
        ON CONFLICT ("UserId", "Kind", "TripId") DO UPDATE SET
          "Count" = CASE WHEN n."ReadAt" IS NULL THEN n."Count" + 1 ELSE 1 END,
          "ReadAt" = NULL,
          "UpdatedAt" = now(),
          "ActorUserId" = EXCLUDED."ActorUserId",
          "RefId" = EXCLUDED."RefId",
          "Amount" = EXCLUDED."Amount"
        """;

    internal const string UpsertIfOlderSql = UpsertSql + """

          WHERE n."UpdatedAt" < @notBefore
        RETURNING n."UserId", n."TripId"
        """;

    internal const string DeleteSomeSql = """
        DELETE FROM "UserNotices" WHERE "Kind" = @kind AND "TripId" = @trip AND "UserId" = ANY(@users)
        """;

    internal const string DeleteAllSql = """
        DELETE FROM "UserNotices" WHERE "Kind" = @kind AND "TripId" = @trip
        """;

    internal const string DeleteForTripSql = """
        DELETE FROM "UserNotices" WHERE "TripId" = @trip AND NOT ("Kind" = ANY(@except))
        """;

    internal const string DeletePairsSql = """
        DELETE FROM "UserNotices" n USING unnest(@kinds, @users) AS d(kind, user_id)
        WHERE n."TripId" = @trip AND n."Kind" = d.kind AND n."UserId" = d.user_id
        """;

    internal const string PushSql = """
        INSERT INTO "OutboxMessages" ("Key", "Exchange", "RoutingKey", "Payload", "CreatedAt", "NextAttemptAt", "Attempts")
        SELECT u.key, @exchange, @routing, u.payload, now(), now() + make_interval(secs => u.delay), 0
        FROM unnest(@keys, @payloads, @delays) AS u(key, payload, delay)
        ON CONFLICT ("Key") DO NOTHING
        """;

    public async Task UpsertAsync(IReadOnlyCollection<NoticeUpsert> notices, CancellationToken ct = default)
    {
        var rows = Distinct(notices);
        if (rows.Count == 0) return;
        await db.Database.ExecuteSqlRawAsync(UpsertSql, UpsertParams(rows), ct);
    }

    public async Task<bool> UpsertIfOlderAsync(NoticeUpsert notice, DateTime notBeforeUtc, CancellationToken ct = default)
        => (await UpsertManyIfOlderAsync([notice], notBeforeUtc, ct)).Count > 0;

    // The same conditional upsert for many rows (morning job); returns the (user, trip) rows that were written.
    public async Task<IReadOnlyList<WrittenNotice>> UpsertManyIfOlderAsync(IReadOnlyCollection<NoticeUpsert> notices, DateTime notBeforeUtc, CancellationToken ct = default)
    {
        var rows = Distinct(notices);
        if (rows.Count == 0) return [];
        var ps = UpsertParams(rows).Append(new NpgsqlParameter("notBefore", NpgsqlDbType.TimestampTz) { Value = Utc(notBeforeUtc) }).ToArray();
        return await db.Database.SqlQueryRaw<WrittenNotice>(UpsertIfOlderSql, ps).ToListAsync(ct);
    }

    public sealed class WrittenNotice
    {
        public Guid UserId { get; set; }
        public Guid TripId { get; set; }
    }

    public async Task DeleteAsync(string kind, Guid tripId, IReadOnlyCollection<Guid>? userIds, CancellationToken ct = default)
    {
        if (userIds is { Count: 0 }) return;
        var kindP = new NpgsqlParameter("kind", NpgsqlDbType.Varchar) { Value = kind };
        var tripP = new NpgsqlParameter("trip", NpgsqlDbType.Uuid) { Value = tripId };
        if (userIds is null)
            await db.Database.ExecuteSqlRawAsync(DeleteAllSql, [kindP, tripP], ct);
        else
            await db.Database.ExecuteSqlRawAsync(DeleteSomeSql, [kindP, tripP, Uuids("users", userIds.Distinct().ToArray())], ct);
    }

    public Task DeleteForTripAsync(Guid tripId, IReadOnlyCollection<string> exceptKinds, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync(DeleteForTripSql,
        [
            new NpgsqlParameter("trip", NpgsqlDbType.Uuid) { Value = tripId },
            new NpgsqlParameter("except", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = exceptKinds.ToArray() },
        ], ct);

    // Several (kind, user) rows of one trip in one statement (payment writes clean a few kinds at once).
    public async Task DeletePairsAsync(Guid tripId, IReadOnlyCollection<(string Kind, Guid UserId)> rows, CancellationToken ct = default)
    {
        var distinct = rows.Distinct().ToList();
        if (distinct.Count == 0) return;
        await db.Database.ExecuteSqlRawAsync(DeletePairsSql,
        [
            new NpgsqlParameter("trip", NpgsqlDbType.Uuid) { Value = tripId },
            new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = distinct.Select(r => r.Kind).ToArray() },
            Uuids("users", distinct.Select(r => r.UserId).ToArray()),
        ], ct);
    }

    public async Task PushAsync(IReadOnlyCollection<PushSpec> pushes, CancellationToken ct = default)
    {
        var rows = pushes.GroupBy(p => p.Key).Select(g => g.Last()).ToList();
        if (rows.Count == 0) return;
        await db.Database.ExecuteSqlRawAsync(PushSql,
        [
            new NpgsqlParameter("exchange", NpgsqlDbType.Varchar) { Value = OutboxTargets.PushExchange },
            new NpgsqlParameter("routing", NpgsqlDbType.Varchar) { Value = OutboxTargets.PushRoutingKey },
            new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => r.Key).ToArray() },
            new NpgsqlParameter("payloads", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(Payload).ToArray() },
            new NpgsqlParameter("delays", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = rows.Select(r => Math.Max(0, (r.Delay ?? TimeSpan.Zero).TotalSeconds)).ToArray() },
        ], ct);
    }

    public void Wake() => signal?.Wake();

    public static string Payload(PushSpec p)
        => JsonSerializer.Serialize(new OutboxPushPayload(
            p.Key, p.UserId, null, p.Title, p.Body, p.Route, p.ArgsJson, p.Tag, 0, p.PushKind, null,
            CollapsingKinds.Contains(p.PushKind) ? CollapsingData : MustArriveData), Json);

    internal static List<NoticeUpsert> Distinct(IReadOnlyCollection<NoticeUpsert> notices)
        => notices.GroupBy(n => (n.UserId, n.Kind, n.TripId)).Select(g => g.Last()).ToList();

    private static NpgsqlParameter[] UpsertParams(IReadOnlyList<NoticeUpsert> rows) =>
    [
        Uuids("users", rows.Select(r => r.UserId).ToArray()),
        new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => r.Kind).ToArray() },
        Uuids("trips", rows.Select(r => r.TripId).ToArray()),
        new NpgsqlParameter("actors", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => r.ActorUserId).ToArray() },
        new NpgsqlParameter("refs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => r.RefId).ToArray() },
        new NpgsqlParameter("amounts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = rows.Select(r => r.Amount).ToArray() },
    ];

    private static NpgsqlParameter Uuids(string name, Guid[] ids) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids };

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
