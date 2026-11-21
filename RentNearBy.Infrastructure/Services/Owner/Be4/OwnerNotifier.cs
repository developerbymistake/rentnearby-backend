using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// Contract 5.3 / 5.4: stored notices are UserNotices rows (ids and kind, upserted in place by INoticeWriter); pushes are rnb.push
// outbox rows relayed to RabbitMQ by OutboxRelayService with publisher confirms. One statement per call, on the caller's context and
// transaction. A push row is written only for an active user with a valid device token, so a logged-out device is never targeted.
public sealed class OwnerNotifier(ApplicationDbContext db, INoticeWriter notices, IOutboxSignal? signal = null) : IOwnerNotifier
{
    public const string Feature = "owner";

    // The JSON property that carries the recipient (the account deletion pins it, R-9).
    public const string UserIdProperty = "userId";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, string> Data = new Dictionary<string, string> { ["feature"] = Feature };

    internal const string PushSql = """
        INSERT INTO "OutboxMessages" ("Key", "Exchange", "RoutingKey", "Payload", "CreatedAt", "NextAttemptAt", "Attempts")
        SELECT u.key, @exchange, @routing, u.payload, now(), now(), 0
        FROM unnest(@keys, @payloads, @users) AS u(key, payload, user_id)
        WHERE EXISTS (SELECT 1 FROM "Users" x WHERE x."Id" = u.user_id AND x."IsActive")
          AND EXISTS (SELECT 1 FROM "DeviceTokens" d WHERE d."UserId" = u.user_id AND d."IsValid")
        ON CONFLICT ("Key") DO NOTHING
        """;

    public Task StoreAsync(IReadOnlyCollection<NoticeUpsert> rows, CancellationToken ct = default)
        => rows.Count == 0 ? Task.CompletedTask : notices.UpsertAsync(rows, ct);

    public Task ClearAsync(string kind, Guid entityId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
        => userIds.Count == 0 ? Task.CompletedTask : notices.DeleteAsync(kind, entityId, userIds, ct);

    public async Task PushAsync(IReadOnlyCollection<OwnerPush> pushes, CancellationToken ct = default)
    {
        var rows = pushes.GroupBy(p => p.Key).Select(g => g.Last()).ToList();
        if (rows.Count == 0) return;
        await db.Database.ExecuteSqlRawAsync(PushSql,
        [
            new NpgsqlParameter("exchange", NpgsqlDbType.Varchar) { Value = OutboxTargets.PushExchange },
            new NpgsqlParameter("routing", NpgsqlDbType.Varchar) { Value = OutboxTargets.PushRoutingKey },
            new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => r.Key).ToArray() },
            new NpgsqlParameter("payloads", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(Payload).ToArray() },
            new NpgsqlParameter("users", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => r.UserId).ToArray() },
        ], ct);
    }

    public void Wake() => signal?.Wake();

    public static string Payload(OwnerPush p)
        => JsonSerializer.Serialize(new OutboxPushPayload(
            p.Key, p.UserId, null, p.Title, p.Body, p.Route, p.ArgsJson, p.Tag, 0, p.Type,
            p.ExpiresAtUtc is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null, Data), Json);
}
