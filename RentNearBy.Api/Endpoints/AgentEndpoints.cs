using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AgentEndpoints
{
    // Mounted at "/api/v1/agents". Everything except the "me" routes below is an admin-managed
    // concern (the consumer app otherwise only ever sees Agents embedded inside
    // InquiryDetailDto.AssignedAgents) — those routes stay AdminOnly. The "me"/"me/leads" routes are
    // the one consumer-facing exception: any authenticated user may call them, since an Agent is a
    // role on an existing account, not a separate identity — access is scoped inside the handler by
    // resolving the caller's own linked Agent, never by a client-supplied id.
    public static RouteGroupBuilder MapAgentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("", AgentHandlers.GetAgents).RequireAuthorization("AdminOnly");
        group.MapGet("/{id:guid}", AgentHandlers.GetAgentById).RequireAuthorization("AdminOnly");
        group.MapPost("", AgentHandlers.AdminCreateAgent).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}", AgentHandlers.AdminUpdateAgent).RequireAuthorization("AdminOnly");
        group.MapDelete("/{id:guid}", AgentHandlers.AdminDeleteAgent).RequireAuthorization("AdminOnly");
        group.MapPost("/{id:guid}/photo", AgentHandlers.AdminUploadAgentPhoto).RequireAuthorization("AdminOnly").DisableAntiforgery();
        group.MapDelete("/{id:guid}/photo", AgentHandlers.AdminDeleteAgentPhoto).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}/categories", AgentHandlers.AdminSetAgentCategories).RequireAuthorization("AdminOnly");

        group.MapGet("/me", AgentHandlers.GetMyAgentProfile).RequireAuthorization();
        group.MapGet("/me/leads", InquiryHandlers.GetMyLeads).RequireAuthorization();
        group.MapGet("/me/leads/{id:guid}", InquiryHandlers.GetMyLeadDetail).RequireAuthorization();
        group.MapPut("/me/leads/{id:guid}/status", InquiryHandlers.UpdateMyLeadStatus).RequireAuthorization();

        return group;
    }
}
