using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;

// Runs on the caller's context, so every statement joins the caller's open transaction. DueAt always comes from the database clock
// (or an absolute instant the caller computed); a second add of the same (subject, kind, round) is a no-op.
public sealed class DueActionScheduler : IDueActionScheduler
{
    private const string Columns = "\"Id\", \"Kind\", \"SubjectId\", \"RoundNo\", \"RequestId\", \"DueAt\", \"State\", \"Attempts\", \"CreatedAt\"";
    private const string OnConflict = "ON CONFLICT (\"SubjectId\", \"Kind\", \"RoundNo\") DO NOTHING";

    public const string InsertAfterDelaySql =
        $"INSERT INTO \"DueActions\" ({Columns}) VALUES (@id, @kind, @subject, @round, @request, now() + make_interval(secs => @delay), '{DueActionStates.Pending}', 0, now()) {OnConflict}";

    public const string InsertAtSql =
        $"INSERT INTO \"DueActions\" ({Columns}) VALUES (@id, @kind, @subject, @round, @request, @dueAt, '{DueActionStates.Pending}', 0, now()) {OnConflict}";

    public const string CancelSql =
        $"UPDATE \"DueActions\" SET \"State\" = '{DueActionStates.Cancelled}', \"DoneAt\" = now(), \"LeaseUntil\" = NULL " +
        $"WHERE \"SubjectId\" = @subject AND \"Kind\" = @kind AND \"State\" = '{DueActionStates.Pending}'";

    public const string CancelForRequestSql =
        $"UPDATE \"DueActions\" SET \"State\" = '{DueActionStates.Cancelled}', \"DoneAt\" = now(), \"LeaseUntil\" = NULL " +
        $"WHERE \"RequestId\" = @request AND \"State\" = '{DueActionStates.Pending}' AND \"Kind\" = ANY(@kinds)";

    public Task AddAsync(ApplicationDbContext db, string kind, Guid subjectId, Guid? requestId, int roundNo, TimeSpan delay, CancellationToken ct = default)
    {
        EnsureKind(kind);
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay cannot be negative");
        return db.Database.ExecuteSqlRawAsync(InsertAfterDelaySql,
            [.. InsertParameters(kind, subjectId, requestId, roundNo), new NpgsqlParameter("delay", NpgsqlDbType.Double) { Value = delay.TotalSeconds }], ct);
    }

    public Task AddAtAsync(ApplicationDbContext db, string kind, Guid subjectId, Guid? requestId, int roundNo, DateTime dueAtUtc, CancellationToken ct = default)
    {
        EnsureKind(kind);
        if (dueAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Due time must be UTC", nameof(dueAtUtc));
        return db.Database.ExecuteSqlRawAsync(InsertAtSql,
            [.. InsertParameters(kind, subjectId, requestId, roundNo), new NpgsqlParameter("dueAt", NpgsqlDbType.TimestampTz) { Value = dueAtUtc }], ct);
    }

    public Task CancelAsync(ApplicationDbContext db, string kind, Guid subjectId, CancellationToken ct = default)
    {
        EnsureKind(kind);
        return db.Database.ExecuteSqlRawAsync(CancelSql,
        [
            new NpgsqlParameter("subject", NpgsqlDbType.Uuid) { Value = subjectId },
            new NpgsqlParameter("kind", NpgsqlDbType.Varchar) { Value = kind },
        ], ct);
    }

    // An empty kind list cancels nothing; callers that end a request pass DueActionKinds.All.
    public Task CancelForRequestAsync(ApplicationDbContext db, Guid requestId, IReadOnlyCollection<string> kinds, CancellationToken ct = default)
    {
        if (kinds.Count == 0) return Task.CompletedTask;
        foreach (var kind in kinds) EnsureKind(kind);
        return db.Database.ExecuteSqlRawAsync(CancelForRequestSql,
        [
            new NpgsqlParameter("request", NpgsqlDbType.Uuid) { Value = requestId },
            new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = kinds.ToArray() },
        ], ct);
    }

    private static NpgsqlParameter[] InsertParameters(string kind, Guid subjectId, Guid? requestId, int roundNo) =>
    [
        new("id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() },
        new("kind", NpgsqlDbType.Varchar) { Value = kind },
        new("subject", NpgsqlDbType.Uuid) { Value = subjectId },
        new("round", NpgsqlDbType.Integer) { Value = roundNo },
        new("request", NpgsqlDbType.Uuid) { Value = requestId.HasValue ? requestId.Value : DBNull.Value },
    ];

    private static void EnsureKind(string kind)
    {
        if (!DueActionKinds.All.Contains(kind)) throw new ArgumentException($"Unknown due action kind '{kind}'", nameof(kind));
    }
}
