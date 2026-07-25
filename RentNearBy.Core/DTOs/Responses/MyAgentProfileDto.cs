namespace RentNearBy.Core.DTOs.Responses;

// GET /agents/me — resolves whether the caller's own account is linked to an Agent. A 404 (not a
// populated-but-empty response) means "not an agent", the expected case for ~all consumer users.
public class MyAgentProfileDto
{
    public Guid AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    // Count of this agent's assigned Inquiries that are unseen: not-Closed AND updated since this
    // agent last saw it (InquiryAgent.SeenAt == null || Inquiry.UpdatedAt > SeenAt) — the "something
    // still needs my attention" badge signal, not a count of every open/live lead.
    public int PendingLeadCount { get; set; }
}
