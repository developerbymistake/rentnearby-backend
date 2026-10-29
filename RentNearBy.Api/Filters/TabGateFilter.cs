using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Filters;

// Route-group gate for the consumer verticals (/listings, /plots, /stays, /stay-jobs, /taxis): 403 TabInactive
// while any of the given tabs is off, so a disabled tab has no reachable API surface. Never applied to the
// /admin/* groups (admin moderates and re-enables while a tab is off), /home (soft per-section gating) or /chat
// (checked per kind on CreateConversation). Routes tagged with SkipTabGateAttribute pass through.
public class TabGateFilter(params string[] tabKeys) : IEndpointFilter
{
    public const string ErrorType = "TabInactive";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.GetEndpoint()?.Metadata.GetMetadata<SkipTabGateAttribute>() != null)
            return await next(context);

        var unitOfWork = http.RequestServices.GetRequiredService<IUnitOfWork>();
        var cache = http.RequestServices.GetRequiredService<IMemoryCache>();
        var tabs = await AppTabCache.GetAsync(unitOfWork, cache);

        if (!TabState.AllActive(tabs, tabKeys))
            return ForbiddenResponse("This feature is currently unavailable.", ErrorType);

        return await next(context);
    }
}
