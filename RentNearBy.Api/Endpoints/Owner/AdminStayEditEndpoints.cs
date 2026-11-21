using RentNearBy.Api.Handlers.Owner.Be3;

namespace RentNearBy.Api.Endpoints.Owner;

// A3 (contract 9, AU-24): the live stay change review queue of the admin app.
public static class AdminStayEditEndpoints
{
    public static IEndpointRouteBuilder MapAdminStayEditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/stays").WithTags("AdminStays").RequireAuthorization("AdminOnly");
        group.MapGet("/pending-edits", AdminStayEditHandlers.List);
        group.MapPost("/{id:guid}/pending-edit/approve", AdminStayEditHandlers.Approve);
        group.MapPost("/{id:guid}/pending-edit/reject", AdminStayEditHandlers.Reject);
        return app;
    }
}
