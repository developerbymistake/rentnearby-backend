using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.DueHandlers;

// O7: drivers accepted, the traveller chose nobody. A visible push to the traveller with the real number of accepted drivers,
// routed to the request status. Skipped once a driver is selected, no accepted driver is left, the request ended or the
// stale expiry has passed. Scoped: one per item.
public abstract class ChooseReminderHandler(TaxiRequestStore store, ITaxiPushWriter pushes) : IDueActionHandler
{
    public abstract string Kind { get; }

    public async Task<DueOutcome> HandleAsync(ApplicationDbContext db, DueActionItem item, CancellationToken ct)
    {
        var request = await store.LockRequestAsync(db, item.SubjectId, ct);
        if (request is null) return DueOutcome.Skipped;
        var live = await store.GetLiveCountsAsync(db, request.Id, ct);
        var now = DateTime.SpecifyKind(await db.Database.SqlQueryRaw<DateTime>("SELECT now() AS \"Value\"").SingleAsync(ct), DateTimeKind.Utc);
        if (!TaxiDispatchRules.ChooseReminderDue(request.Status, live.Accepted, now, request.DateFrom)) return DueOutcome.Skipped;

        await pushes.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.ChooseDriver, request.TravellerUserId, request.TripId, request.Id, Count: live.Accepted), ct);
        return DueOutcome.Done;
    }
}

public sealed class ChooseReminderFirstHandler(TaxiRequestStore store, ITaxiPushWriter pushes) : ChooseReminderHandler(store, pushes)
{
    public override string Kind => DueActionKinds.ChooseReminderFirst;
}

public sealed class ChooseReminderSecondHandler(TaxiRequestStore store, ITaxiPushWriter pushes) : ChooseReminderHandler(store, pushes)
{
    public override string Kind => DueActionKinds.ChooseReminderSecond;
}
