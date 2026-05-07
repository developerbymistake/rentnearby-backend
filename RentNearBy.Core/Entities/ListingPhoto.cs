namespace RentNearBy.Core.Entities;

public class ListingPhoto
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string PhotoUrl { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int PhotoOrder { get; set; }
    public DateTime UploadedAt { get; set; }

    public Listing Listing { get; set; } = null!;
}
