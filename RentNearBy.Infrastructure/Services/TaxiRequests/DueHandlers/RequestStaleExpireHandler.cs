using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.DueHandlers;

// MASTER_PLAN 2.7 and P6: the first needed day is over (00:00 IST after DateFrom). A request that is still Open, Collecting
// or Selected ends as Expired NotBooked; a booked or ended request is skipped. Scoped: one instance per item.
public sealed class RequestStaleExpireHandler(TaxiRequestStore store) : IDueActionHandler, IDueActionAfterCommit
{
    public static readonly IReadOnlyList<string> StaleStates = [RequestStates.Open, RequestStates.Collecting, RequestStates.Selected];

    private TaxiClosureResult? _signals;

    public string Kind => DueActionKinds.RequestStaleExpire;

    public async Task<DueOutcome> HandleAsync(ApplicationDbContext db, DueActionItem item, CancellationToken ct)
    {
        var request = await store.LockRequestAsync(db, item.SubjectId, ct);
        if (request is null || !StaleStates.Contains(request.Status)) return DueOutcome.Skipped;

        var end = await store.EndRequestAsync(db, request.Id, RequestStates.Expired, RequestEndReasons.NotBooked, ct: ct);
        if (end is null) return DueOutcome.Skipped;
        _signals = end.Signals;
        return DueOutcome.Done;
    }

    public Task AfterCommitAsync(DueActionItem item, DueOutcome outcome, CancellationToken ct)
        => _signals is { } signals ? store.AfterCommitAsync(signals) : Task.CompletedTask;
}
