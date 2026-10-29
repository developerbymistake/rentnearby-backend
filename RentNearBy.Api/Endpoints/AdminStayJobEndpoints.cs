using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AdminStayJobEndpoints
{
    public static RouteGroupBuilder MapAdminStayJobEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", AdminStayJobHandlers.GetJobs).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/close", AdminStayJobHandlers.CloseJob).RequireAuthorization("AdminOnly");
        group.MapDelete("/{id:guid}", AdminStayJobHandlers.DeleteJob).RequireAuthorization("AdminOnly");

        group.MapGet("/lookups", AdminStayJobHandlers.GetAllLookups).RequireAuthorization("AdminOnly");
        group.MapPost("/lookups/{kind}", AdminStayJobHandlers.CreateLookup).RequireAuthorization("AdminOnly");
        group.MapPut("/lookups/{kind}/{id:guid}", AdminStayJobHandlers.UpdateLookup).RequireAuthorization("AdminOnly");
        group.MapDelete("/lookups/{kind}/{id:guid}", AdminStayJobHandlers.DeleteLookup).RequireAuthorization("AdminOnly");

        return group;
    }
}
