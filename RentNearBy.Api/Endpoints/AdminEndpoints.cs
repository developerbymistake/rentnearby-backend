using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/cities", AdminHandlers.GetCities);
        group.MapPost("/cities", AdminHandlers.CreateCity).RequireAuthorization("AdminOnly");
        group.MapDelete("/cities/{id:guid}", AdminHandlers.DeleteCity).RequireAuthorization("AdminOnly");

        group.MapGet("/districts", AdminHandlers.GetDistricts);
        group.MapPost("/districts", AdminHandlers.CreateDistrict).RequireAuthorization("AdminOnly");
        group.MapDelete("/districts/{id:guid}", AdminHandlers.DeleteDistrict).RequireAuthorization("AdminOnly");

        group.MapGet("/room-types", AdminHandlers.GetRoomTypes);
        group.MapPost("/room-types", AdminHandlers.CreateRoomType).RequireAuthorization("AdminOnly");
        group.MapDelete("/room-types/{id:guid}", AdminHandlers.DeleteRoomType).RequireAuthorization("AdminOnly");

        group.MapGet("/stats", AdminHandlers.GetStats).RequireAuthorization("AdminOnly");

        return group;
    }
}
