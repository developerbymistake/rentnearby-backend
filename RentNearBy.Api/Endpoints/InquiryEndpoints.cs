using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class InquiryEndpoints
{
    // Consumer-facing, mounted at "/api/v1/inquiries" — any authenticated user.
    public static RouteGroupBuilder MapInquiryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("", InquiryHandlers.CreateInquiry).RequireAuthorization();
        group.MapGet("/mine", InquiryHandlers.GetMyInquiries).RequireAuthorization();
        group.MapGet("/active-count", InquiryHandlers.GetMyActiveInquiryCount).RequireAuthorization();
        group.MapGet("/{id:guid}", InquiryHandlers.GetInquiryDetail).RequireAuthorization();
        group.MapPost("/{id:guid}/escalate", InquiryHandlers.EscalateInquiry).RequireAuthorization();

        return group;
    }

    // Admin pipeline management, mounted at "/api/v1/admin/inquiries".
    public static RouteGroupBuilder MapAdminInquiryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("", InquiryHandlers.AdminGetInquiries).RequireAuthorization("AdminOnly");
        group.MapGet("/{id:guid}", InquiryHandlers.AdminGetInquiryDetail).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/status", InquiryHandlers.AdminUpdateInquiryStatus).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/agents", InquiryHandlers.AdminSetInquiryAgents).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/escalation/resolve", InquiryHandlers.AdminResolveEscalation).RequireAuthorization("AdminOnly");

        return group;
    }
}
