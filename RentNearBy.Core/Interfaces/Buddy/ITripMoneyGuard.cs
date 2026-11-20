using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Core.Interfaces.Buddy;

public interface ITripMoneyGuard
{
    // Reads TripExpenseState first (1 statement); only when OpenPayments > 0 loads travellers + ledger. Empty = may be deleted.
    Task<IReadOnlyList<OpenPaymentLine>> GetOpenPaymentsAsync(Guid tripId, CancellationToken ct = default);
}
