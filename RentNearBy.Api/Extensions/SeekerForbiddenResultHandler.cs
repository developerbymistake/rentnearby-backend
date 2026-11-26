using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace RentNearBy.Api.Extensions;

// A failed SeekerOnly requirement answers the standard envelope (403, error.type SeekerOnly); every other result keeps the
// framework behaviour (empty 403 for CanListStays / CanListTaxis / AdminOnly, 401 challenge).
public sealed class SeekerForbiddenResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (SeekerGate.FailedSeekerRequirement(authorizeResult))
        {
            await SeekerGate.Forbidden().ExecuteAsync(context);
            return;
        }
        await _fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}
