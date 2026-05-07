namespace RentNearBy.Core.Entities;

public class District
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<City> Cities { get; set; } = new List<City>();
    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
}
