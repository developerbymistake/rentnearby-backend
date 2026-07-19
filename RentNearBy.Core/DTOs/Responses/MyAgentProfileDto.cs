namespace RentNearBy.Core.DTOs.Responses;

// GET /agents/me — resolves whether the caller's own account is linked to an Agent. A 404 (not a
// populated-but-empty response) means "not an agent", the expected case for ~all consumer users.
public class MyAgentProfileDto
{
    public Guid AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    // Count of this agent's assigned Inquiries still at Status == Submitted — the "something new
    // landed" badge signal, not a count of every open/live lead (see the plan's Design decisions).
    public int PendingLeadCount { get; set; }
}
