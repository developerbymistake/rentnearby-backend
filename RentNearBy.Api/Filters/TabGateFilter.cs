using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.Interfaces;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Filters;

// Route-group-level gate for the consumer app's Rooms/Plots/Services verticals — rejects every request
// under a gated group with 403 the moment an admin flips that tab's AppTab.IsActive off, so a disabled
// tab has zero reachable API surface (not just a hidden nav icon). Applied per-group in Program.cs to
// the consumer-facing route groups only (/listings, /plots, /services, /agents, /enquiries) — never to
// the matching /admin/* groups, which must stay reachable so an admin can moderate existing data or
// re-enable the tab while it's off. Chat and the public /web-enquiry flow are deliberately not gated:
// chat is a shared Room+Plot entity with no single owning tab, and web-enquiry is a separate website
// surface, not part of the mobile app's tab system.
public class TabGateFilter(string tabKey) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var unitOfWork = context.HttpContext.RequestServices.GetRequiredService<IUnitOfWork>();
        var cache = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();

        if (!await ConfigHandlers.IsTabActiveCachedAsync(tabKey, unitOfWork, cache))
            return ForbiddenResponse("This feature is currently unavailable.");

        return await next(context);
    }
}
