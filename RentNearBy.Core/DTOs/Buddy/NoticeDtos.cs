namespace RentNearBy.Core.DTOs.Buddy;

// N1 item: ids, kind and numbers only; the app builds the words. TripId null for BuddyRequest.
public class NoticeItemDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid? TripId { get; set; }
    public string? TripTitle { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }
    public Guid? RefId { get; set; }
    public int? Amount { get; set; }
    // ExpenseAdded, ShareChanged: live expense title (null once deleted). TripJoined: owner + joined viewers, live.
    public string? RefTitle { get; set; }
    public int? Travellers { get; set; }
    public int Count { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsRead { get; set; }
    public string Route { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> Args { get; set; } = new Dictionary<string, string>();
}

public class NoticePageDto
{
    public IReadOnlyList<NoticeItemDto> Items { get; set; } = [];
    public string? NextCursor { get; set; }
    public int Unread { get; set; }
}
