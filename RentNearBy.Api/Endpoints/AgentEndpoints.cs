using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

public static class AgentEndpoints
{
    // Mounted at "/api/v1/agents" — entirely an admin-managed concern (no consumer-facing use case;
    // the consumer app only ever sees an Agent embedded inside InquiryDetailDto), so every route here
    // is admin-only, including reads.
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

        return group;
    }
}
