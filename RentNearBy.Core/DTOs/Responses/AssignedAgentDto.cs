namespace RentNearBy.Core.DTOs.Responses;

// Identity-only view of an Agent as seen from inside an Inquiry (the assigned-agent card on both
// the consumer's own Inquiry Detail and the admin's) — deliberately omits Phone/WhatsAppNumber.
// Contact here is one-directional: the agent reaches out to the customer using the Inquiry's own
// FullName/Mobile, never the other way around, so nothing in this view needs to expose how to
// reach the agent directly. Full agent contact stays on AgentDto for the dedicated Agent CRUD
// (GET /agents) and the agent's own /agents/me* views.
public class AssignedAgentDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PhotoUrl { get; set; } = string.Empty;
}
