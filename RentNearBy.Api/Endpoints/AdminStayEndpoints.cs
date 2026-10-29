using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AdminStayEndpoints
{
    public static RouteGroupBuilder MapAdminStayEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/types", AdminStayHandlers.GetStayTypes);
        group.MapPost("/types", AdminStayHandlers.CreateStayType).RequireAuthorization("AdminOnly");
        group.MapPut("/types/{id:guid}", AdminStayHandlers.UpdateStayType).RequireAuthorization("AdminOnly");
        group.MapDelete("/types/{id:guid}", AdminStayHandlers.DeleteStayType).RequireAuthorization("AdminOnly");

        group.MapGet("/", AdminStayHandlers.GetStays).RequireAuthorization("AdminOnly");
        group.MapGet("/{id:guid}", AdminStayHandlers.GetStayById).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/approve", AdminStayHandlers.ApproveStay).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/reject", AdminStayHandlers.RejectStay).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/unpublish", AdminStayHandlers.UnpublishStay).RequireAuthorization("AdminOnly");
        group.MapDelete("/{id:guid}", AdminStayHandlers.DeleteStay).RequireAuthorization("AdminOnly");

        return group;
    }
}
