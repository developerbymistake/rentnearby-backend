namespace RentNearBy.Core.Entities;

public class RoomListing
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid RoomTypeId { get; set; }
    public string? Description { get; set; }
    public int PriceMonthly { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? Address { get; set; }
    public Guid DistrictId { get; set; }
    public Guid? CityId { get; set; }
    public bool IsActive { get; set; } = false;
    public string FurnishedStatus { get; set; } = "None";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    public DateTime? ValidUntil { get; set; }

    public User User { get; set; } = null!;
    public RoomType RoomType { get; set; } = null!;
    public District District { get; set; } = null!;
    public City? City { get; set; }
    public ICollection<RoomPhoto> Photos { get; set; } = new List<RoomPhoto>();
}
