namespace RentNearBy.Core.DTOs.Social;

public class RoomTripDto
{
    public Guid TripId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public long CityKey { get; set; }
    // Up to 3 other members, members with a photo first: PhotoUrls[i] and MemberInitials[i] are the same person.
    public List<string> PhotoUrls { get; set; } = [];
    public List<string> MemberInitials { get; set; } = [];
}

// Contract 2.1 Room.
public class ChatRoomDto
{
    public Guid RoomId { get; set; }
    public string Kind { get; set; } = "D";
    public string Status { get; set; } = "Active";
    public bool IStarted { get; set; }
    public SocialPersonDto? Peer { get; set; }
    public RoomTripDto? Trip { get; set; }
    public string? Subtitle { get; set; }
    public bool PeerFollowsMe { get; set; }
    public bool IFollowPeer { get; set; }
    public long LastSeq { get; set; }
    public long LastReadSeq { get; set; }
    public long? PeerLastReadSeq { get; set; }
    public int Unread { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public string? Preview { get; set; }
    public bool CanSend { get; set; }
    public string Lock { get; set; } = "none";
}

// Contract 2.1 Message.
public class ChatMessageDto
{
    public long Seq { get; set; }
    public Guid? SenderId { get; set; }
    public string? SenderName { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool Masked { get; set; }
    public string? System { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public Guid? ClientMessageId { get; set; }
}

public class RoomsPageDto
{
    public List<ChatRoomDto> Items { get; set; } = [];
    public string? NextCursor { get; set; }
    public ChatUnreadDto Unread { get; set; } = new();
}

public class ChatPreflightDto
{
    public ChatRoomDto? Room { get; set; }
    public SocialPersonDto Peer { get; set; } = new();
    public bool CanMessage { get; set; }
    public string Lock { get; set; } = "none";
    public LimitUseDto NewChats { get; set; } = new();
    public bool IsFree { get; set; }
}

public class StartChatRequest
{
    public Guid ToUserId { get; set; }
    public Guid ClientMessageId { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class StartedChatDto
{
    public ChatRoomDto Room { get; set; } = new();
    public ChatMessageDto Message { get; set; } = new();

    // Server-only: the room already existed, so the start was a plain send (no new-chat permit spent). Never on the wire.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IntoExistingRoom { get; set; }
}

public class SendChatMessageRequest
{
    public Guid ClientMessageId { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class ChatReadRequest
{
    public long Seq { get; set; }
}

public class ChatReadResultDto
{
    public ChatUnreadDto Unread { get; set; } = new();
}

public class ChatMessagesPageDto
{
    public List<ChatMessageDto> Items { get; set; } = [];
    public long LastSeq { get; set; }
    public long? PeerLastReadSeq { get; set; }
}

// Hub answer of DmSend: {ok:true, message:Message} or {ok:false, type, message, data}.
public class DmSendResultDto
{
    public bool Ok { get; set; }
    public object? Message { get; set; }
    public string? Type { get; set; }
    public object? Data { get; set; }
}
