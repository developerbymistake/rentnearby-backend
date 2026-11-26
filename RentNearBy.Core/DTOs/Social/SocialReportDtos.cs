namespace RentNearBy.Core.DTOs.Social;

public class SocialReportRequest
{
    public string Scope { get; set; } = string.Empty;
    public Guid? TripId { get; set; }
    public Guid? RoomId { get; set; }
    public long? Seq { get; set; }
    public Guid ReasonId { get; set; }
    public string? Note { get; set; }
    public bool AlsoBlock { get; set; }
}

public class SocialReportedDto
{
    public bool Reported { get; set; }
    public bool Blocked { get; set; }
}

public class SnapshotMessageDto
{
    public long Seq { get; set; }
    public Guid? SenderId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsReported { get; set; }
}

public class MessageSnapshotDto
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public long? AnchorSeq { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<SnapshotMessageDto> Messages { get; set; } = [];
}

// The trip post block of an admin report row.
public class ReportTripPostDto
{
    public Guid TripId { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<string> CityNames { get; set; } = [];
    public string Text { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
}
