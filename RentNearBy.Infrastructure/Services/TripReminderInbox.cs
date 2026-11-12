using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// The host's morning reminder also lands in the Notifications inbox, so a host with no push token still sees it on opening the app.
public static class TripReminderInbox
{
    // Same key as the push, so a second run of the day maps to the same inbox row.
    public static Guid RowId(string pushKey)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes("bakhli:reminder-inbox:" + pushKey));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    public static IReadOnlyList<NotificationEvent> ForHosts(IEnumerable<ReminderPlan> claimed, DateTime now)
        => claimed.Where(p => p.Category == ReminderCategory.Host).Select(p => new NotificationEvent
        {
            Id = RowId(p.Push.Key),
            TargetUserId = p.UserId,
            Type = NotificationTypes.TripReminder,
            Title = p.Push.Title,
            Body = p.Push.Body,
            ActionRoute = p.Push.Route,
            ActionArgumentsJson = p.Push.ArgsJson,
            CreatedAt = now,
        }).ToList();

    // One set-based insert for the whole chunk, in the caller's transaction. A user deleted since the query is skipped, a repeat is a no-op.
    public static async Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<NotificationEvent> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "NotificationEvents" ("Id", "TargetUserId", "Type", "Title", "Body", "ActionRoute", "ActionArgumentsJson", "CreatedAt")
            SELECT x.id, x.uid, @type, x.title, x.body, x.route, x.args::jsonb, @now
            FROM unnest(@ids, @uids, @titles, @bodies, @routes, @args) AS x(id, uid, title, body, route, args)
            WHERE EXISTS (SELECT 1 FROM "Users" u WHERE u."Id" = x.uid)
            ON CONFLICT ("Id") DO NOTHING
            """, conn, tx);
        cmd.Parameters.AddWithValue("type", NotificationTypes.TripReminder);
        cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = rows[0].CreatedAt });
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => r.Id).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("uids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => r.TargetUserId!.Value).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("titles", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => r.Title).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("bodies", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => r.Body).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("routes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => r.ActionRoute ?? string.Empty).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("args", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => r.ActionArgumentsJson ?? "{}").ToArray() });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
