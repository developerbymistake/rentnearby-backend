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
    public DateTime? ValidUntil { get; set; }
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public PlotType PlotType { get; set; } = null!;
    public User User { get; set; } = null!;
    public District District { get; set; } = null!;
    public City? City { get; set; }
    public ICollection<PlotPhoto> Photos { get; set; } = new List<PlotPhoto>();
}
