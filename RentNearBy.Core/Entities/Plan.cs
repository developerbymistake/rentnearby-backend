namespace RentNearBy.Core.Entities;

// Catalog row: one per sellable (or default free) plan of a feature (ListingKinds.*). Limits live in
// PlanFeatures, prices in PlanPrices; code reads feature keys, never plan names or codes.
public class Plan
{
    public Guid Id { get; set; }
    public string FeatureKey { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Tagline { get; set; }
    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int TrialDays { get; set; }
    public int SortOrder { get; set; }
    public string? PlayProductId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<PlanFeature> Features { get; set; } = new List<PlanFeature>();
    public ICollection<PlanPrice> Prices { get; set; } = new List<PlanPrice>();
}
