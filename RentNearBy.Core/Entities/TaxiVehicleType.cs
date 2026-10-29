namespace RentNearBy.Core.Entities;

public class TaxiVehicleType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public short? DefaultSeats { get; set; }
    public int SortOrder { get; set; } = 999;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ICollection<TaxiListing> TaxiListings { get; set; } = new List<TaxiListing>();
}
