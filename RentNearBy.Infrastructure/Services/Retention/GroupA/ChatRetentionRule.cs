using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services.Retention.GroupA;

public sealed class ChatRetentionRule : IRetentionRule
{
    public const int ConversationBatch = 200;

    public string Name => "Chat";
    public string Group => RetentionGroups.Chat;
    public int DefaultDays => 90;
    public bool DefaultEnabled => true;
    public bool GuardExempt => false;

    // Any one of these keeps the conversation: an enquiry in use, a booking trail, a recent or open report.
    public const string ProtectionSql =
        "NOT EXISTS (SELECT 1 FROM \"TripEnquiries\" e WHERE e.\"ConversationId\" = c.\"Id\" AND (" +
            "e.\"Status\" IN " + TripEnquiryStatuses.OpenSqlList +
            " OR e.\"UpdatedAt\" >= @cutoff OR e.\"Status\" = 'Failed'" +
            " OR e.\"ConfirmedByTravellerAt\" IS NOT NULL OR e.\"ConfirmedByOwnerAt\" IS NOT NULL OR e.\"BookedByTravellerAt\" IS NOT NULL" +
            " OR e.\"AdvanceReceivedAt\" IS NOT NULL OR e.\"AdvanceStatus\" <> 'None' OR e.\"AcceptedOfferId\" IS NOT NULL" +
            " OR (e.\"TripId\" IS NOT NULL AND EXISTS (SELECT 1 FROM \"Trips\" tr WHERE tr.\"Id\" = e.\"TripId\" AND tr.\"Status\" <> 'Completed' AND (tr.\"EndDate\" IS NULL OR tr.\"EndDate\" >= @today))))) " +
        "AND NOT EXISTS (SELECT 1 FROM \"ListingReports\" r WHERE (r.\"Status\" <> 'Resolved' OR COALESCE(r.\"ResolvedAt\", r.\"CreatedAt\") >= @cutoff) AND (" +
            "r.\"TripEnquiryId\" IN (SELECT e2.\"Id\" FROM \"TripEnquiries\" e2 WHERE e2.\"ConversationId\" = c.\"Id\")" +
            " OR (r.\"ReporterUserId\" = c.\"RenterId\" AND r.\"ReportedUserId\" = c.\"OwnerId\")" +
            " OR (r.\"ReporterUserId\" = c.\"OwnerId\" AND r.\"ReportedUserId\" = c.\"RenterId\")" +
            " OR (r.\"ListingId\" = c.\"ListingId\" AND r.\"ListingType\" = c.\"ListingType\")))";

    // Old by its last activity (also covers empty conversations) and no message newer than the cutoff.
    public const string EligibleSql =
        "GREATEST(c.\"LastMessageAt\", c.\"CreatedAt\") < @cutoff " +
        "AND NOT EXISTS (SELECT 1 FROM \"Messages\" m WHERE m.\"ConversationId\" = c.\"Id\" AND m.\"CreatedAt\" >= @cutoff) AND " + ProtectionSql;

    public const string SelectIdsSql =
        "SELECT c.\"Id\" FROM \"Conversations\" c WHERE " + EligibleSql + " ORDER BY c.\"LastMessageAt\" LIMIT @n FOR UPDATE SKIP LOCKED";

    public const string CountSql =
        "SELECT count(*) FROM (SELECT 1 FROM \"Conversations\" c WHERE " + EligibleSql + " LIMIT @cap) x";

    const string StillEligibleIds =
        "m.\"ConversationId\" IN (SELECT c.\"Id\" FROM \"Conversations\" c WHERE c.\"Id\" = ANY(@ids) AND " + ProtectionSql + ")";

    // Answers first: RespondsToMessageId is a Restrict self-FK.
    public const string DeleteAnswersSql =
        "DELETE FROM \"Messages\" m WHERE m.\"ConversationId\" = ANY(@ids) AND m.\"RespondsToMessageId\" IS NOT NULL AND m.\"CreatedAt\" < @cutoff AND " + StillEligibleIds;

    public const string DeleteRestSql =
        "DELETE FROM \"Messages\" m WHERE m.\"ConversationId\" = ANY(@ids) AND m.\"CreatedAt\" < @cutoff AND " + StillEligibleIds +
        " AND NOT EXISTS (SELECT 1 FROM \"Messages\" a WHERE a.\"RespondsToMessageId\" = m.\"Id\")";

    public const string DeleteConversationsSql =
        "DELETE FROM \"Conversations\" c WHERE c.\"Id\" = ANY(@ids) AND " +
        "NOT EXISTS (SELECT 1 FROM \"Messages\" m WHERE m.\"ConversationId\" = c.\"Id\") AND " + ProtectionSql;

    public async Task<long> EstimateTableRowsAsync(RetentionContext ctx, CancellationToken ct)
    {
        var estimate = await ScalarAsync(ctx, null,
            "SELECT reltuples::bigint FROM pg_class WHERE oid = to_regclass('\"Conversations\"')", [], ct);
        if (estimate >= 1000) return estimate;
        return await ScalarAsync(ctx, null, "SELECT count(*) FROM \"Conversations\"", [], ct);
    }

    public Task<long> CountCandidatesAsync(RetentionContext ctx, long cap, CancellationToken ct)
        => ScalarAsync(ctx, null, CountSql, [.. Common(ctx), new NpgsqlParameter("cap", cap + 1)], ct);

    public async Task<int> DeleteBatchAsync(RetentionContext ctx, int batchSize, CancellationToken ct)
    {
        var n = Math.Clamp(batchSize, 1, ConversationBatch);
        await using var tx = await ctx.Db.Database.BeginTransactionAsync(ct);
        var dbTx = tx.GetDbTransaction();
        var ids = await SelectIdsAsync(ctx, dbTx, n, ct);
        if (ids.Length == 0) return 0;

        var p = new List<NpgsqlParameter>(Common(ctx)) { new("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids } };
        var deleted = await ExecAsync(ctx, dbTx, DeleteAnswersSql, p, ct);
        deleted += await ExecAsync(ctx, dbTx, DeleteRestSql, p, ct);
        deleted += await ExecAsync(ctx, dbTx, DeleteConversationsSql, p, ct);
        await tx.CommitAsync(ct);
        return deleted;
    }

    static NpgsqlParameter[] Common(RetentionContext ctx) =>
    [
        new("cutoff", NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(ctx.CutoffUtc, DateTimeKind.Utc) },
        new("today", NpgsqlDbType.Date) { Value = ctx.TodayIst },
    ];

    static async Task<Guid[]> SelectIdsAsync(RetentionContext ctx, DbTransaction tx, int n, CancellationToken ct)
    {
        await using var cmd = Command(ctx, tx, SelectIdsSql, [.. Common(ctx), new NpgsqlParameter("n", n)]);
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return [.. ids];
    }

    static async Task<int> ExecAsync(RetentionContext ctx, DbTransaction? tx, string sql, IEnumerable<NpgsqlParameter> ps, CancellationToken ct)
    {
        await using var cmd = Command(ctx, tx, sql, ps);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    static async Task<long> ScalarAsync(RetentionContext ctx, DbTransaction? tx, string sql, IEnumerable<NpgsqlParameter> ps, CancellationToken ct)
    {
        await using var cmd = Command(ctx, tx, sql, ps);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    static NpgsqlCommand Command(RetentionContext ctx, DbTransaction? tx, string sql, IEnumerable<NpgsqlParameter> ps)
    {
        var conn = (NpgsqlConnection)ctx.Db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        var cmd = new NpgsqlCommand(sql, conn) { Transaction = (NpgsqlTransaction?)tx };
        foreach (var p in ps) cmd.Parameters.Add(p);
        return cmd;
    }
}
