using System.Security.Claims;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Extensions;

// The one place that knows the traveller rule (SPEC R1): the SeekerOnly policy on a route, the in-handler test of a mixed
// route, and the 403 body the app maps.
public static class SeekerGate
{
    public static bool IsSeeker(ClaimsPrincipal principal) => principal.HasClaim(ClaimTypes.Role, UserRoles.Seeker);

    public static IResult Forbidden() => ApiResults.ForbiddenResponse(AuthPolicies.SeekerOnlyMessage, AuthPolicies.ErrorType);

    public static RouteHandlerBuilder SeekerOnly(this RouteHandlerBuilder route) => route.RequireAuthorization(AuthPolicies.SeekerOnly);

    public static bool FailedSeekerRequirement(PolicyAuthorizationResult result)
        => result.Forbidden
           && result.AuthorizationFailure != null
           && result.AuthorizationFailure.FailedRequirements
               .OfType<ClaimsAuthorizationRequirement>()
               .Any(r => r.ClaimType == ClaimTypes.Role && r.AllowedValues != null && r.AllowedValues.Contains(UserRoles.Seeker));
}
