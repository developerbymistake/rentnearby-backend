namespace RentNearBy.Core.Entities;

// Composite-key many-to-many, exact shape of AgentServiceCategory — no surrogate Id. An Inquiry can
// have multiple Agents simultaneously assigned (every active Agent mapped to its ServiceCategory,
// or whichever set Admin picks manually); every assigned Agent sees the lead in their own My Leads
// and any of them can update its status.
public class InquiryAgent
{
    public Guid InquiryId { get; set; }
    public Guid AgentId { get; set; }
    public DateTime AssignedAt { get; set; }
    // Per-agent seen-timestamp lives on this join row (not on Inquiry) because multiple agents can be
    // co-assigned to one inquiry — a single column on Inquiry would mark it "seen" for every agent the
    // instant any one of them opens it.
    public DateTime? SeenAt { get; set; }

    public Inquiry Inquiry { get; set; } = null!;
    public Agent Agent { get; set; } = null!;
}
