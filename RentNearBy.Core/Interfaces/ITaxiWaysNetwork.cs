using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

// Singleton holder of the in-memory route network; rebuilt when TaxiWaysVersion moves.
public interface ITaxiWaysNetwork
{
    // null only when no snapshot exists yet and the first build failed.
    Task<TaxiWaysNetworkSnapshot?> GetAsync(CancellationToken ct = default);
    string? CurrentVersion { get; }
}
