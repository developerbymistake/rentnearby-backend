using RentNearBy.Api.Handlers.Owner.Be2;

namespace RentNearBy.Api.Endpoints.Owner;

public static class FestivalEndpoints
{
    // F1 on /api/v1/festivals (any signed-in user).
    public static RouteGroupBuilder MapFestivalEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("", FestivalHandlers.List).RequireAuthorization();
        return group;
    }

    // A2 on /api/v1/admin (AdminOnly).
    public static RouteGroupBuilder MapAdminFestivalEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/festivals", AdminFestivalHandlers.List).RequireAuthorization("AdminOnly");
        group.MapGet("/festivals/states", AdminFestivalHandlers.States).RequireAuthorization("AdminOnly");
        group.MapPost("/festivals", AdminFestivalHandlers.Create).RequireAuthorization("AdminOnly");
        group.MapPut("/festivals/{id:guid}", AdminFestivalHandlers.Update).RequireAuthorization("AdminOnly");
        group.MapDelete("/festivals/{id:guid}", AdminFestivalHandlers.Delete).RequireAuthorization("AdminOnly");
        return group;
    }
}
