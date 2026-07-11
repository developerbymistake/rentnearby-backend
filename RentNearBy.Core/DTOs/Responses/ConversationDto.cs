namespace RentNearBy.Core.DTOs.Responses;

public class ConversationDto
{
    public Guid Id { get; set; }
    public string ListingType { get; set; } = string.Empty; // "Room" | "Plot"
    public Guid ListingId { get; set; }
    public string ListingTitle { get; set; } = string.Empty;
    public string? ListingThumbnailUrl { get; set; }
    public Guid OtherPartyId { get; set; }
    public string OtherPartyName { get; set; } = string.Empty;
    public bool IsOwner { get; set; } // is the current caller the owner side of this conversation?
    public string Status { get; set; } = string.Empty; // "Active" | "Blocked" | "ListingRemoved" | "ListingInactive"
    public DateTime LastMessageAt { get; set; }
    public string? LastMessagePreview { get; set; }
    public int UnreadCount { get; set; } // resolved to the caller's side
}
