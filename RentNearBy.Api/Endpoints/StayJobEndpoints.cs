using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class StayJobEndpoints
{
    public static RouteGroupBuilder MapStayJobEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/lookups", StayJobHandlers.GetLookups);
        group.MapGet("/", StayJobHandlers.GetBrowse);

        group.MapGet("/me", StayJobHandlers.GetMine).RequireAuthorization("CanListStays");
        group.MapGet("/me/{id:guid}", StayJobHandlers.GetMineById).RequireAuthorization("CanListStays");
        group.MapPost("/me", StayJobHandlers.Create).RequireAuthorization("CanListStays");
        group.MapPut("/me/{id:guid}", StayJobHandlers.Update).RequireAuthorization("CanListStays");
        group.MapPut("/me/{id:guid}/close", StayJobHandlers.Close).RequireAuthorization("CanListStays");

        group.MapGet("/{id:guid}", StayJobHandlers.GetById);

        return group;
    }
}
