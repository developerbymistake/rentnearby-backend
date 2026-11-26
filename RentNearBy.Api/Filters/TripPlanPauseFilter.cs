using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers.Social;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Api.Filters;

// PLAN_STATES_SOCIAL 3: a trip kept inactive after Plus ended is read-only on every /trips route (owner and travellers alike).
// Reads, delete, leave, member removal and the money settling pass (SeekerPlanStates.WriteAllowed); any other write answers
// 409 TripInactiveByPlan {limit}. One primary-key read, only for writes that name a trip id.
// Its services are resolved only for a write that names a trip id, so reads never touch the database.
public class TripPlanPauseFilter : IEndpointFilter
{
    private static readonly string[] TripKeys = ["id", "tripId"];

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var pattern = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (SeekerPlanStates.WriteAllowed(http.Request.Method, pattern)) return await next(context);

        var tripId = TripIdOf(http.Request.RouteValues);
        if (tripId is not { } id) return await next(context);

        var db = http.RequestServices.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Trips.AsNoTracking()
            .Where(t => t.Id == id && t.InactiveByPlan)
            .Select(t => (Guid?)t.OwnerUserId)
            .FirstOrDefaultAsync(http.RequestAborted);
        if (owner is not { } ownerId) return await next(context);

        var limits = await http.RequestServices.GetRequiredService<ISeekerLimits>().GetPlanAsync(ownerId, http.RequestAborted);
        return SocialHttp.Error(SocialErrorTypes.TripInactiveByPlan, new { limit = limits.ActiveTrips });
    }

    private static Guid? TripIdOf(RouteValueDictionary values)
    {
        foreach (var key in TripKeys)
            if (values.TryGetValue(key, out var raw) && Guid.TryParse(raw?.ToString(), out var id)) return id;
        return null;
    }
}
