using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface ITaxiWaysNetworkSource
{
    // Every route of a public taxi (approved, available, not deleted), with Ok geometry when present.
    Task<IReadOnlyList<TaxiWaysSourceRow>> LoadAsync(CancellationToken ct = default);
}
