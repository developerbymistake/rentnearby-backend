namespace RentNearBy.Core.Entities;

public class RoomPhoto
{
    public Guid Id { get; set; }
    public Guid RoomListingId { get; set; }
    public string PhotoUrl { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int PhotoOrder { get; set; }
    public DateTime UploadedAt { get; set; }

    public RoomListing RoomListing { get; set; } = null!;
}
