using RentNearBy.Api.Handlers;
using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Api.Filters;

// Profile picture upload (any role): user:photo:{userId}, 10 an hour, never given back. Own bucket, no owner write bucket.
public sealed class UserPhotoLimitFilter(IRateLimitService limiter) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!UsersHandlers.TryGetUserId(http.User, out var userId)) return await next(context);
        var photo = OwnerRateLimitKeys.ProfilePhoto(userId);
        var check = await limiter.CheckAsync(photo.Key, photo.Max, photo.Window);
        if (!check.IsAllowed) return OwnerHttp.Denied(http, check, photo.Window);
        return await next(context);
    }
}

public static class UserPhotoLimitExtensions
{
    public static RouteHandlerBuilder UserPhotoLimited(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<UserPhotoLimitFilter>();
}
