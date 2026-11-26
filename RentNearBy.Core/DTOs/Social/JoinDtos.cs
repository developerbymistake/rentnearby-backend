namespace RentNearBy.Core.DTOs.Social;

public class SendJoinRequest
{
    public string? Note { get; set; }
    public Guid ClientRequestId { get; set; }
}

public class JoinSentDto
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class JoinRequestItemDto
{
    public Guid Id { get; set; }
    public PersonRowDto Person { get; set; } = new();
    public int Tier { get; set; }
    public string? Note { get; set; }
    public List<string> Interests { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public class JoinRequestsPageDto
{
    public PublicTripDto Trip { get; set; } = new();
    public List<JoinRequestItemDto> Items { get; set; } = [];
}

public class AcceptJoinRequest
{
    public bool AddToBuddies { get; set; }
}

public class JoinAcceptedDto
{
    public Guid MemberId { get; set; }
    public int SeatsLeft { get; set; }
    public Guid RoomId { get; set; }
}
