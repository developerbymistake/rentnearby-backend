namespace RentNearBy.Core.Entities;

public class RoomType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; } = 999;
    public DateTime CreatedAt { get; set; }

    public ICollection<RoomListing> RoomListings { get; set; } = new List<RoomListing>();
}
