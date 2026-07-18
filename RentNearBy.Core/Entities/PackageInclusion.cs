namespace RentNearBy.Core.Entities;

// Composite-key many-to-many, exact shape of BannerDismissal — no surrogate Id.
public class PackageInclusion
{
    public Guid ServicePackageId { get; set; }
    public Guid InclusionId { get; set; }

    public ServicePackage ServicePackage { get; set; } = null!;
    public Inclusion Inclusion { get; set; } = null!;
}
