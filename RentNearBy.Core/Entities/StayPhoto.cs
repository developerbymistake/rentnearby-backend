namespace RentNearBy.Core.Entities;

public class StayPhoto
{
    public Guid Id { get; set; }
    public Guid StayListingId { get; set; }
    public string PhotoUrl { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int PhotoOrder { get; set; }
    public DateTime UploadedAt { get; set; }
}
