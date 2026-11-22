using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Api.Filters;

// FB-11 / FB-52 (NFR 8.4): every /stays/me and /taxis/me write takes the owner write limit (30 a minute, refused
// business writes give the permit back) and the attempt bucket (120 a minute, never given back). Added per route.
public sealed class OwnerWriteLimitFilter(IRateLimitService limiter) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!OwnerHttp.TryOwner(http.User, out var userId)) return await next(context);
        object? raw = null;
        var result = await OwnerHttp.WriteLimitAsync(limiter, http, OwnerRateLimitKeys.Write(userId), async () =>
        {
            raw = await next(context);
            return raw as IResult ?? Results.Empty;
        });
        return raw is not null and not IResult && OwnerHttp.IsSuccess(result) ? raw : result;
    }
}

// FB-11: a photo upload also takes owner:photo:{userId}, 20 an hour, never given back (each one costs a Cloudinary upload).
public sealed class OwnerPhotoLimitFilter(IRateLimitService limiter) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!OwnerHttp.TryOwner(http.User, out var userId)) return await next(context);
        var photo = OwnerRateLimitKeys.Photo(userId);
        var check = await limiter.CheckAsync(photo.Key, photo.Max, photo.Window);
        if (!check.IsAllowed) return OwnerHttp.Denied(http, check, photo.Window);
        return await next(context);
    }
}

public static class OwnerWriteLimitExtensions
{
    public static RouteHandlerBuilder OwnerWriteLimited(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<OwnerWriteLimitFilter>();

    public static RouteHandlerBuilder OwnerPhotoLimited(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<OwnerPhotoLimitFilter>().AddEndpointFilter<OwnerWriteLimitFilter>();
}
