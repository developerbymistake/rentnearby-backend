namespace RentNearBy.Core.Entities;

// Composite-key many-to-many, exact shape of AgentService — no surrogate Id. An Enquiry can
// have multiple Agents simultaneously assigned (every active Agent mapped to its Service,
// or whichever set Admin picks manually); every assigned Agent sees the lead in their own My Leads
// and any of them can update its status.
public class EnquiryAgent
{
    public Guid EnquiryId { get; set; }
    public Guid AgentId { get; set; }
    public DateTime AssignedAt { get; set; }
    // Per-agent seen-timestamp lives on this join row (not on Enquiry) because multiple agents can be
    // co-assigned to one enquiry — a single column on Enquiry would mark it "seen" for every agent the
    // instant any one of them opens it.
    public DateTime? SeenAt { get; set; }

    public Enquiry Enquiry { get; set; } = null!;
    public Agent Agent { get; set; } = null!;
}
