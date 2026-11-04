using Microsoft.Extensions.Options;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Filters;

// Kill switch for the trip booking routes (TripExecution:Enabled / TRIP_EXECUTION_ENABLED); added per route.
public class TripExecutionGateFilter(IOptionsMonitor<TripExecutionOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!options.CurrentValue.Enabled)
            return ForbiddenResponse("Trip bookings are paused.", TripEnquiryErrorTypes.TripExecutionDisabled);

        return await next(context);
    }
}
