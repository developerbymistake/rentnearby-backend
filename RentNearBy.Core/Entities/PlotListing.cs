namespace RentNearBy.Core.Entities;

public class PlotListing
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public decimal AreaValue { get; set; }
    public string AreaUnit { get; set; } = string.Empty;  // sqft/bigha/acre/nali
    public decimal AreaSqft { get; set; }                 // stored for sorting (approx conversion)
    public Guid PlotTypeId { get; set; }
    public string? Description { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? Address { get; set; }
    public Guid DistrictId { get; set; }
    public Guid? CityId { get; set; }
    public bool IsActive { get; set; } = false;
    // Generated once at creation time (SlugGenerator, in PlotListingHandlers.CreatePlotListing) — immutable
    // for the life of the listing. Powers the public share link (bakhli.com/l/p/{slug}) and the /go/p/{slug}
    // QR redirect; never regenerated on update, and never derived from price/status (see SlugGenerator.cs).
    public string? Slug { get; set; }
    public DateTime? ValidUntil { get; set; }
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DigestNotifiedAt { get; set; }

    // Go-Live moderation — null means this listing has never gone through a Go-Live request.
    public string? LiveRequestStatus { get; set; }
    public string? RejectedReason { get; set; }
    // Snapshot of the plan selected/spent at request time, so approval computes ValidUntil from
    // this snapshot rather than re-resolving the (admin-mutable) CreditPlan.
    public string? RequestedPlanType { get; set; }
    public int? RequestedPlanDays { get; set; }
    public int? RequestedPlanCreditsSpent { get; set; }

    public PlotType PlotType { get; set; } = null!;
    public User User { get; set; } = null!;
    public District District { get; set; } = null!;
    public City? City { get; set; }
    public ICollection<PlotPhoto> Photos { get; set; } = new List<PlotPhoto>();
}
