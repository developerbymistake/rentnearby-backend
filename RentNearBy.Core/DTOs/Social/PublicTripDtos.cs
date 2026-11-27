using System.Text.Json.Serialization;

namespace RentNearBy.Core.DTOs.Social;

// {kind:"within", months:N} | {kind:"month", month:yyyymm}. Never a day.
public class WhenDto
{
    public string Kind { get; set; } = "within";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Months { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Month { get; set; }
}

// Month-only projection of a public trip (contract 1.5): no dates, days, stay, taxi, money or member data.
public class PublicTripDto
{
    public Guid TripId { get; set; }
    public SocialPersonDto Owner { get; set; } = new();
    public List<string> OwnerInterests { get; set; } = [];
    public bool IsMine { get; set; }
    public long CityKey { get; set; }
    public List<string> CityNames { get; set; } = [];
    public string? StateName { get; set; }
    public WhenDto When { get; set; } = new();
    public int Days { get; set; }
    public string JoinGender { get; set; } = "A";
    public int Seats { get; set; }
    public int SeatsLeft { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
    public string MyJoin { get; set; } = "none";
    public int? WaitingRequests { get; set; }
    public bool IFollowOwner { get; set; }
    public MutualsDto? Mutuals { get; set; }
    public bool Sample { get; set; }
    public bool NotOpen { get; set; }
    // Post notify (owner 2026-11-27): 50 requests are waiting, the post takes no more until the host answers some ("Full").
    public bool RequestsFull { get; set; }
    // Owner's own post only (absent for others): the nearby toggle was accepted, and when the 07:00 IST digest told travellers.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NotifyNearby { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? NearbyNotifiedAt { get; set; }
    // Owner's own post only (absent for others and when unlocked): the 72-hour post lock ends at this time (UTC); until then the
    // post cannot be made private or deleted (409 PostLocked).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? UnlocksAt { get; set; }
    // Only on the answer of the create / publish call to the poster: what his followers and nearby travellers are told.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PostNotifyDto? Notify { get; set; }
}

// followers: sent | none (no follower wants trip news) | weekLimit (publishes silently) | alreadySent (edit or re-publish).
// nearby: off | queued (told at the next 07:00 IST) | weekLimit | tooFar (starts after 60 days) | private (private profile) |
// alreadySent. used / limit: notified posts in the rolling 7 days after this call; nextAllowedAt: when one more may notify.
public class PostNotifyDto
{
    public string Followers { get; set; } = "none";
    public string Nearby { get; set; } = "off";
    public int Used { get; set; }
    public int Limit { get; set; }
    public DateTime? NextAllowedAt { get; set; }
}

public class TripFeedDto
{
    public List<PublicTripDto> Mine { get; set; } = [];
    public List<PublicTripDto> Items { get; set; } = [];
    public string? NextCursor { get; set; }
}

public class TripRailDto
{
    public List<PublicTripDto> Items { get; set; } = [];
}

public class CreatePublicTripRequest
{
    public Guid ClientTripId { get; set; }
    public List<long> CityKeys { get; set; } = [];
    public string WhenKind { get; set; } = string.Empty;
    public int? WithinMonths { get; set; }
    public DateOnly? StartDate { get; set; }
    public int Days { get; set; }
    public string JoinGender { get; set; } = "A";
    public int Seats { get; set; }
    public string Text { get; set; } = string.Empty;
    // "Notify travellers near me" (default off); honoured only on the publish that notifies.
    public bool NotifyNearby { get; set; }
}

public class UpdatePublicPostRequest
{
    public string WhenKind { get; set; } = string.Empty;
    public int? WithinMonths { get; set; }
    public DateOnly? StartDate { get; set; }
    public int Days { get; set; }
    public string JoinGender { get; set; } = "A";
    public int Seats { get; set; }
    public string Text { get; set; } = string.Empty;
    // "Notify travellers near me" (default off); read only when this call publishes a post that never notified.
    public bool NotifyNearby { get; set; }
}
