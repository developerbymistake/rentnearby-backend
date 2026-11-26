namespace RentNearBy.Core.DTOs.Buddy;

public class FindBuddyRequest
{
    public string? Query { get; set; }
    public string? Source { get; set; }
}

public class FoundPersonDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public string? Username { get; set; }
    public string Initial { get; set; } = string.Empty;
    public string Relation { get; set; } = string.Empty;
    public Guid? RequestId { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class FindBuddyResponse
{
    public bool Found { get; set; }
    public FoundPersonDto? Person { get; set; }
}

// Same JSON as { "found": false } only.
public class NotFoundBuddyResponse
{
    public bool Found { get; set; }
}

public class SendBuddyRequest
{
    public Guid ToUserId { get; set; }
    public string? Source { get; set; }
    public Guid ClientRequestId { get; set; }
}

public class BuddyRequestCreatedDto
{
    public Guid RequestId { get; set; }
    public string State { get; set; } = "pending";
    public DateTime ExpiresAt { get; set; }
}

public class BuddyRequestAcceptedDto
{
    public string State { get; set; } = "accepted";
    public PersonDto Buddy { get; set; } = new();
}

public class BuddyAcceptDto
{
    public PersonDto Buddy { get; set; } = new();
}

public class ReceivedRequestDto
{
    public Guid RequestId { get; set; }
    public PersonDto Person { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class SentRequestDto
{
    public Guid RequestId { get; set; }
    public PersonDto Person { get; set; } = new();
    public string State { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool CanCancel { get; set; }
}

public class BuddyItemDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public string? Username { get; set; }
    public string Initial { get; set; } = string.Empty;
    public bool IsNew { get; set; }
    public DateTime Since { get; set; }
    public string? TripState { get; set; }
    // Male / Female / Other (public profile gender), trips we are both on, people I follow who follow him (capped 99).
    public string? Gender { get; set; }
    public int TripsTogether { get; set; }
    public int MutualCount { get; set; }
}

public class CountedPageDto<T>
{
    public IReadOnlyList<T> Items { get; set; } = [];
    public int Count { get; set; }
    public string? NextCursor { get; set; }
}

public class BuddySeenRequest
{
    public List<Guid> UserIds { get; set; } = [];
}

public class BuddySummaryDto
{
    public int Buddies { get; set; }
    public int NewBuddies { get; set; }
    public int ReceivedPending { get; set; }
    public int Blocked { get; set; }
}

public class AlsoReportDto
{
    public Guid ReasonId { get; set; }
    public string? Note { get; set; }
}

public class BlockPersonRequest
{
    public AlsoReportDto? AlsoReport { get; set; }
    public string? Context { get; set; }
}

public class BlockedPersonDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public string? Username { get; set; }
    public string Initial { get; set; } = string.Empty;
    public DateTime BlockedAt { get; set; }
}

public class ReportPersonRequest
{
    public Guid ReasonId { get; set; }
    public string? Note { get; set; }
    public bool AlsoBlock { get; set; }
    public string? Context { get; set; }
}

public class UserReportReasonDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class TripInCommonDto
{
    public Guid TripId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string MyRole { get; set; } = string.Empty;
}

public class BlockContextDto
{
    public PersonDto? Person { get; set; }
    public List<TripInCommonDto> TripsInCommon { get; set; } = [];
}

public class BuddyHoldRequest
{
    public DateTime? Until { get; set; }
}

public class BuddyHoldDto
{
    public Guid UserId { get; set; }
    public DateTime? BuddyRequestsHeldUntil { get; set; }
}

public class PersonReportCountsDto
{
    public int Reports30d { get; set; }
    public int Reporters30d { get; set; }
    public int BlocksReceived30d { get; set; }
    public int RequestsSent7d { get; set; }
    public int RequestsAccepted7d { get; set; }
}
