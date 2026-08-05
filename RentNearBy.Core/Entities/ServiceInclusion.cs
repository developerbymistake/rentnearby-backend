namespace RentNearBy.Core.Entities;

// Composite-key many-to-many, exact shape of BannerDismissal — no surrogate Id.
// Service-scoped (not ServicePackage-scoped): every package within a Service shares the same
// inclusion set, so the relation belongs on the Service, not duplicated per group-size tier.
public class ServiceInclusion
{
    public Guid ServiceId { get; set; }
    public Guid InclusionId { get; set; }

    public Service Service { get; set; } = null!;
    public Inclusion Inclusion { get; set; } = null!;
}
