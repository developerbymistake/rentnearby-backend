namespace RentNearBy.Core.Entities;

// Composite-key many-to-many, exact shape of BannerDismissal — no surrogate Id. One Agent may serve
// multiple Services.
public class AgentService
{
    public Guid AgentId { get; set; }
    public Guid ServiceId { get; set; }

    public Agent Agent { get; set; } = null!;
    public Service Service { get; set; } = null!;
}
