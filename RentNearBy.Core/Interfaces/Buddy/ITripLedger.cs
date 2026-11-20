using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Core.Interfaces.Buddy;

// The one place that reads money.
public interface ITripLedger
{
    // 3 statements: grouped money, payments of the trip (not Withdrawn, newest 300), state row.
    Task<TripLedgerSnapshot> LoadAsync(Guid tripId, IReadOnlyList<Guid> canonicalOrder, CancellationToken ct = default);

    // The same 3 statements for many trips.
    Task<IReadOnlyDictionary<Guid, TripLedgerSnapshot>> LoadManyAsync(
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> canonicalOrderByTrip, CancellationToken ct = default);

    // LAST step of every money write, inside its transaction, under the trip lock: grouped money + payments, then ONE
    // upsert of TripExpenseState. changedExpenseId null for payment writes.
    Task<TripLedgerSnapshot> RefreshStateAsync(Guid tripId, IReadOnlyList<Guid> canonicalOrder, Guid? changedExpenseId, CancellationToken ct = default);
}
