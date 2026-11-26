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
}
