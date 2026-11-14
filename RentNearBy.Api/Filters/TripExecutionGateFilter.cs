using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Filters;

// Master switch for the trip booking routes (admin setting, cached in memory); added per route.
public class TripExecutionGateFilter(ITripExecutionFeatureReader reader) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!await reader.IsEnabledAsync())
            return ForbiddenResponse("Trip bookings are paused.", TripEnquiryErrorTypes.TripExecutionDisabled);

        return await next(context);
    }
}
