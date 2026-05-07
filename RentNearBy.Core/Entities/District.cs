namespace RentNearBy.Core.Entities;

public class District
{
    public Guid Id { get; set; }
    public Guid CityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public DateTime CreatedAt { get; set; }

    public City City { get; set; } = null!;
    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
}
