using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface ITaxiWaysSearch
{
    // null = the network is not available (handler answers 503).
    Task<TaxiWaysResultDto?> SearchAsync(TaxiWaysQuery q, CancellationToken ct = default);
    // null = the key does not match the current network; throws TaxiWaysUnavailableException when there is no network.
    Task<WayPlanDetailDto?> PlanAsync(string planKey, int people, CancellationToken ct = default);
    Task<WayDriverPageDto?> LegDriversAsync(string legKey, LegDriverQuery q, CancellationToken ct = default);
}
