namespace RentNearBy.Core.Entities;

public class Conversation
{
    public Guid Id { get; set; }
    public Guid RenterId { get; set; }
    public Guid OwnerId { get; set; }
    public string ListingType { get; set; } = string.Empty; // "Room" | "Plot"
    public Guid ListingId { get; set; }
    public string Status { get; set; } = "Active"; // "Active" | "Blocked" | "ListingRemoved" | "ListingInactive"
    public DateTime CreatedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
    public string? LastMessagePreview { get; set; }
    public int UnreadCountForRenter { get; set; }
    public int UnreadCountForOwner { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();
}
