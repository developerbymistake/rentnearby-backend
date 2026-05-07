namespace RentNearBy.Core.Entities;

public class RoomType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
}
