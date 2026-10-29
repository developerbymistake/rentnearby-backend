namespace RentNearBy.Core.DTOs.Responses;

// GET /plans/catalog. Built from PlanCatalogCache; disabled plans are omitted.
public class PlanCatalogDto
{
    public List<PlanCatalogFeatureDto> Features { get; set; } = new();
}

public class PlanCatalogFeatureDto
{
    public string FeatureKey { get; set; } = string.Empty;
    public List<PlanCatalogItemDto> Plans { get; set; } = new();
}

public class PlanCatalogItemDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Tagline { get; set; }
    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; }
    public int TrialDays { get; set; }
    public Dictionary<string, int> Features { get; set; } = new();
    public List<PlanPriceDto> Prices { get; set; } = new();
}

public class PlanPriceDto
{
    public int CycleMonths { get; set; }
    public int PriceInr { get; set; }
    public int PerMonthInr { get; set; }
    public int SavePercent { get; set; }
}
