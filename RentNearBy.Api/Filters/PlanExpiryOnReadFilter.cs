using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Api.Filters;

// Matrix L6: the owner reads (/plans/me, /stays/me, /taxis/me, /stay-jobs/me, /owner/home) first expire the caller's own
// non-Play plan row whose end time passed, so limits and rows agree on the first request instead of after the 5-minute pass.
public sealed class PlanExpiryOnReadFilter(PlanExpiryOnRead expiry) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (OwnerHttp.TryOwner(context.HttpContext.User, out var userId))
            await expiry.ExpireDueAsync(userId, context.HttpContext.RequestAborted);
        return await next(context);
    }
}

public static class PlanExpiryOnReadExtensions
{
    public static RouteHandlerBuilder ExpiresPlanOnRead(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<PlanExpiryOnReadFilter>();
}
