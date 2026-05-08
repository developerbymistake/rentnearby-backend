namespace RentNearBy.Core.Entities;

public class Listing
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid RoomTypeId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int PriceMonthly { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? Address { get; set; }
    public Guid DistrictId { get; set; }
    public Guid? CityId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
    public RoomType RoomType { get; set; } = null!;
    public District District { get; set; } = null!;
    public City? City { get; set; }
    public ICollection<ListingPhoto> Photos { get; set; } = new List<ListingPhoto>();
}
