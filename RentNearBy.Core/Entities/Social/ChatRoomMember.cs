namespace RentNearBy.Core.Entities.Social;

// Table "ChatRoomMembers". ListBucket P People, T Trips, R Requests, X hidden.
public class ChatRoomMember
{
    public Guid RoomId { get; set; }
    public Guid UserId { get; set; }
    public long LastReadSeq { get; set; }
    public int UnreadCount { get; set; }
    public string ListBucket { get; set; } = "P";
    public long ClearedUpToSeq { get; set; }
    public long JoinedSeq { get; set; }
    public DateTime? LeftAt { get; set; }
    public DateTime SortAt { get; set; }
}
