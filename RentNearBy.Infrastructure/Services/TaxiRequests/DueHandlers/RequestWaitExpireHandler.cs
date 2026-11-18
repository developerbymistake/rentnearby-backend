using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.DueHandlers;

// MASTER_PLAN 2.7: the 10 minute wait (decision 11) is over. When the request still waits, nothing rings and no driver
// accepted or is selected, it ends as Expired AllDriversBusy. Any other state, a wait that was served meanwhile, or a wait
// that started again later is skipped (the later wait has its own timer). Scoped: one instance per item.
public sealed class RequestWaitExpireHandler(TaxiRequestStore store) : IDueActionHandler, IDueActionAfterCommit
{
    public static readonly string WaitOverSql = $"""
        SELECT ("QueueWaitStartedAt" IS NOT NULL AND "QueueWaitStartedAt" + interval '{TaxiDispatchRules.QueueWaitSeconds} seconds' <= now()) AS "Value"
        FROM "TaxiRequests" WHERE "Id" = @id
        """;

    public static readonly string EndRoundSql = $"""
        UPDATE "TaxiRequests" SET "QueueWaitStartedAt" = NULL
        WHERE "Id" = @id AND "WaitRound" = @round AND "QueueWaitStartedAt" IS NOT NULL
          AND "QueueWaitStartedAt" + interval '{TaxiDispatchRules.QueueWaitSeconds} seconds' <= now()
        """;

    private TaxiClosureResult? _signals;

    public string Kind => DueActionKinds.RequestWaitExpire;

    public async Task<DueOutcome> HandleAsync(ApplicationDbContext db, DueActionItem item, CancellationToken ct)
    {
        var request = await store.LockRequestAsync(db, item.SubjectId, ct);
        if (request is null || !RequestStates.Dispatching.Contains(request.Status)) return DueOutcome.Skipped;

        var over = await db.Database.SqlQueryRaw<bool>(WaitOverSql, new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = request.Id }).SingleAsync(ct);
        if (!over) return DueOutcome.Skipped;

        var live = await store.GetLiveCountsAsync(db, request.Id, ct);
        if (live.Ringing > 0 || live.Accepted > 0 || live.Selected > 0)
        {
            // This round ends without expiring the request: the next Wait outcome starts a new round with its own timer.
            await db.Database.ExecuteSqlRawAsync(EndRoundSql,
                [new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = request.Id }, new NpgsqlParameter("round", NpgsqlDbType.Integer) { Value = item.RoundNo }], ct);
            return DueOutcome.Skipped;
        }

        var end = await store.EndRequestAsync(db, request.Id, RequestStates.Expired, RequestEndReasons.AllDriversBusy, ct: ct);
        if (end is null) return DueOutcome.Skipped;
        _signals = end.Signals;
        return DueOutcome.Done;
    }

    public Task AfterCommitAsync(DueActionItem item, DueOutcome outcome, CancellationToken ct)
        => _signals is { } signals ? store.AfterCommitAsync(signals) : Task.CompletedTask;
}
