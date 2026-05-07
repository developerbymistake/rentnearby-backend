namespace RentNearBy.Core.Entities;

public class City
{
    public Guid Id { get; set; }
    public Guid DistrictId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public DateTime CreatedAt { get; set; }

    public District District { get; set; } = null!;
    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
}
