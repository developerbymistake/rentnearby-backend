namespace RentNearBy.Core.DTOs.Responses;

// Shared between the agent's own Dashboard (GET /agents/me/stats) and the admin's Agent Stats page
// (GET /agents/{id}/stats) — both are built from the same BuildLeadStatsDto assembly in
// AgentHandlers.cs, so the shape stays identical whether the caller is the agent themself or admin.
public class AgentLeadStatsDto
{
    public Guid AgentId { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public int Year { get; set; }
    public int TotalLeads { get; set; }
    public int TotalSubmitted { get; set; }
    public int TotalContacted { get; set; }
    public int TotalClosed { get; set; }

    // Always 12 entries, Month 1 (Jan) through 12 (Dec), zero-filled for months with no leads.
    public List<MonthlyLeadStatDto> Months { get; set; } = new();
}

public class MonthlyLeadStatDto
{
    public int Month { get; set; } // 1-12
    public int Submitted { get; set; }
    public int Contacted { get; set; }
    public int Closed { get; set; }
    public int Total { get; set; }
}

// Internal row shape for the raw GroupBy query result — never serialized to a client directly,
// only consumed by AgentHandlers.BuildLeadStatsDto to assemble the DTOs above.
public class MonthlyStatusCountRow
{
    public int Month { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Count { get; set; }
}
