using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.Owner.Quote;

namespace RentNearBy.Api.Filters;

// Stay quotation own buckets (ARCHITECTURE 9.2): quote:photo 10/hour, quote:reserve 30/hour, quote:name 3/day. A permit is taken
// before anything else runs and is never given back (unlike the owner write bucket, which returns permits on 400/409).
public abstract class QuoteLimitFilter(IRateLimitService limiter) : IEndpointFilter
{
    protected abstract OwnerRateLimit LimitOf(Guid userId);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!OwnerHttp.TryOwner(http.User, out var userId)) return await next(context);
        var limit = LimitOf(userId);
        var check = await limiter.CheckAsync(limit.Key, limit.Max, limit.Window);
        if (!check.IsAllowed) return OwnerHttp.Denied(http, check, limit.Window);
        return await next(context);
    }
}

public sealed class QuotePhotoLimitFilter(IRateLimitService limiter) : QuoteLimitFilter(limiter)
{
    protected override OwnerRateLimit LimitOf(Guid userId) => QuoteRateLimits.Photo(userId);
}

public sealed class QuoteReserveLimitFilter(IRateLimitService limiter) : QuoteLimitFilter(limiter)
{
    protected override OwnerRateLimit LimitOf(Guid userId) => QuoteRateLimits.Reserve(userId);
}

public sealed class QuoteNameLimitFilter(IRateLimitService limiter) : QuoteLimitFilter(limiter)
{
    protected override OwnerRateLimit LimitOf(Guid userId) => QuoteRateLimits.NameChange(userId);
}

public static class QuoteLimitExtensions
{
    // The own bucket runs first (outermost), then the owner write bucket.
    public static RouteHandlerBuilder QuotePhotoLimited(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<QuotePhotoLimitFilter>().OwnerWriteLimited();

    public static RouteHandlerBuilder QuoteReserveLimited(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<QuoteReserveLimitFilter>().OwnerWriteLimited();

    public static RouteHandlerBuilder QuoteNameLimited(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<QuoteNameLimitFilter>().OwnerWriteLimited();
}
