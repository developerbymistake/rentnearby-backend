using Microsoft.Extensions.Options;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Filters;

// Config gate for /trips (TripPlanner:Enabled / TRIP_PLANNER_ENABLED); the planner is not a tab.
public class TripPlannerGateFilter(IOptions<TripPlannerOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!options.Value.Enabled)
            return ForbiddenResponse("Trip planner is not available right now.", TripErrorTypes.TripPlannerDisabled);

        return await next(context);
    }
}
