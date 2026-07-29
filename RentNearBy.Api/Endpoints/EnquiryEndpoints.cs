using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class EnquiryEndpoints
{
    // Consumer-facing, mounted at "/api/v1/enquiries" — any authenticated user.
    public static RouteGroupBuilder MapEnquiryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("", EnquiryHandlers.CreateEnquiry).RequireAuthorization();
        group.MapGet("/mine", EnquiryHandlers.GetMyEnquiries).RequireAuthorization();
        group.MapGet("/active-count", EnquiryHandlers.GetMyActiveEnquiryCount).RequireAuthorization();
        group.MapGet("/{id:guid}", EnquiryHandlers.GetEnquiryDetail).RequireAuthorization();
        group.MapPost("/{id:guid}/escalate", EnquiryHandlers.EscalateEnquiry).RequireAuthorization();
        group.MapPut("/{id:guid}/seen", EnquiryHandlers.MarkEnquirySeen).RequireAuthorization();

        return group;
    }

    // Admin pipeline management, mounted at "/api/v1/admin/enquiries".
    public static RouteGroupBuilder MapAdminEnquiryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("", EnquiryHandlers.AdminGetEnquiries).RequireAuthorization("AdminOnly");
        group.MapGet("/{id:guid}", EnquiryHandlers.AdminGetEnquiryDetail).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/status", EnquiryHandlers.AdminUpdateEnquiryStatus).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/agents", EnquiryHandlers.AdminSetEnquiryAgents).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/escalation/resolve", EnquiryHandlers.AdminResolveEscalation).RequireAuthorization("AdminOnly");

        return group;
    }
}
