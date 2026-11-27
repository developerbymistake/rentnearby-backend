using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Spec C: the level-up congratulation. Written by TripCompletionCounter in the completing transaction, right after the +1 of
// Users.CompletedTripsCount, only when the new count is the first number of a level (1, 10, 30, 80, 200, 500) and the owner is an
// active seeker. One statement: the Notifications inbox row (id derived from the key) and the outbox push (unique Key), both
// ON CONFLICT DO NOTHING, so a retry or a second call never writes a second row or push. It does not depend on the
// messages / follows switches; delivery follows the device push permission only.
public static class LevelUpNotice
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal const string WriteSql = """
        WITH n AS (
          INSERT INTO "NotificationEvents" ("Id", "TargetUserId", "Type", "Title", "Body", "ActionRoute", "ActionArgumentsJson", "CreatedAt")
          SELECT @id, @user, @type, @title, @body, @route, @args::jsonb, now()
          WHERE EXISTS (SELECT 1 FROM "Users" x WHERE x."Id" = @user)
          ON CONFLICT ("Id") DO NOTHING)
        INSERT INTO "OutboxMessages" ("Key", "Exchange", "RoutingKey", "Payload", "CreatedAt", "NextAttemptAt", "Attempts")
        SELECT @key, @exchange, @routing, @payload, now(), now(), 0
        WHERE EXISTS (SELECT 1 FROM "Users" x WHERE x."Id" = @user)
        ON CONFLICT ("Key") DO NOTHING
        """;

    // The inbox row id of one (user, level): the same key always maps to the same row.
    public static Guid RowId(string key)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes("bakhli:levelup-inbox:" + key));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    // Seeker only (the Travel levels page is a traveller page); null = nothing to send for this count.
    public static LevelUpCopy? CopyFor(Guid userId, int newCount, string? name, bool isActiveSeeker)
        => isActiveSeeker && TravelLevels.ReachedAt(newCount) is { } level ? LevelUpPushCopy.For(userId, level, newCount, name) : null;

    public static string Payload(Guid userId, Guid rowId, LevelUpCopy copy)
        => JsonSerializer.Serialize(new OutboxPushPayload(
            copy.Key, userId, rowId, copy.Title, copy.Body, LevelUpPushCopy.Route, LevelUpPushCopy.ArgsJson, LevelUpPushCopy.Tag(userId), 0,
            NotificationTypes.TravelLevelUp), Json);

    public static Task<int> WriteInTxAsync(ApplicationDbContext db, Guid userId, LevelUpCopy copy, CancellationToken ct = default)
    {
        var rowId = RowId(copy.Key);
        return db.Database.ExecuteSqlRawAsync(WriteSql,
        [
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = rowId },
            new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = userId },
            new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = NotificationTypes.TravelLevelUp },
            new NpgsqlParameter("title", NpgsqlDbType.Text) { Value = copy.Title },
            new NpgsqlParameter("body", NpgsqlDbType.Text) { Value = copy.Body },
            new NpgsqlParameter("route", NpgsqlDbType.Text) { Value = LevelUpPushCopy.Route },
            new NpgsqlParameter("args", NpgsqlDbType.Text) { Value = LevelUpPushCopy.ArgsJson },
            new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = copy.Key },
            new NpgsqlParameter("exchange", NpgsqlDbType.Varchar) { Value = OutboxTargets.PushExchange },
            new NpgsqlParameter("routing", NpgsqlDbType.Varchar) { Value = OutboxTargets.PushRoutingKey },
            new NpgsqlParameter("payload", NpgsqlDbType.Text) { Value = Payload(userId, rowId, copy) },
        ], ct);
    }
}
