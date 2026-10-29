namespace RentNearBy.Core.DTOs.Responses;

// Admin catalog view: every plan including disabled ones, with features and prices.
public class AdminPlanDto
{
    public Guid Id { get; set; }
    public string FeatureKey { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Tagline { get; set; }
    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; }
    public int TrialDays { get; set; }
    public int SortOrder { get; set; }
    public List<AdminPlanFeatureDto> Features { get; set; } = new();
    public List<AdminPlanPriceDto> Prices { get; set; } = new();
    public bool LowPriceWarning { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AdminPlanFeatureDto
{
    public string Key { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class AdminPlanPriceDto
{
    public Guid Id { get; set; }
    public int CycleMonths { get; set; }
    public int PriceInr { get; set; }
    public string? RazorpayPlanId { get; set; }
    public bool IsEnabled { get; set; }
}
