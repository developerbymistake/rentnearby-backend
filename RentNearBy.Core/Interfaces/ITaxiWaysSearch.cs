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
    // Server fares of these taxis on the stretch of legKey, from the same links the search priced (one per taxi). A taxi not on
    // the stretch, an unknown key or no network is simply absent. excludeRoute: the previous car's route (BuildPlan's rule).
    Task<Dictionary<Guid, LegTaxiFare>> LegFaresAsync(
        string legKey, IReadOnlyCollection<Guid> taxiIds, Guid? excludeRoute = null, CancellationToken ct = default);
}
