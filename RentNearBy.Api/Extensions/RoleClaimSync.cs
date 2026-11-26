using System.Security.Claims;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Extensions;

// The role claim of a user token is replaced by the live role of the session's user at validation, so a token issued
// before a role choice cannot keep a role the account no longer has.
public static class RoleClaimSync
{
    public static void Apply(ClaimsPrincipal principal, Guid roleId)
    {
        var key = UserRoles.KeyOf(roleId);
        var roles = principal.FindAll(ClaimTypes.Role).ToList();
        if (roles.Count == 1 && roles[0].Value == key && !principal.HasClaim(c => c.Type == UserRoles.ClaimType))
            return;

        foreach (var identity in principal.Identities)
        {
            foreach (var claim in identity.Claims.Where(c => c.Type == ClaimTypes.Role || c.Type == UserRoles.ClaimType).ToList())
                identity.TryRemoveClaim(claim);
        }
        var target = principal.Identities.FirstOrDefault(i => i.IsAuthenticated) ?? principal.Identities.First();
        target.AddClaim(new Claim(ClaimTypes.Role, key));
    }
}
