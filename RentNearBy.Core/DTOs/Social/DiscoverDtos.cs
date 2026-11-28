using System.Text.Json.Serialization;

namespace RentNearBy.Core.DTOs.Social;

// Discover answers (DESIGN 1.1). Standalone: nothing a locked private profile hides.
public class DiscoverPersonDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string Initial { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public int Level { get; set; }
    public bool FollowsMe { get; set; }
    public bool IFollow { get; set; }
    public bool FollowRequested { get; set; }
    public bool IsPrivate { get; set; }
    public DiscoverReasonDto Reason { get; set; } = new();
}

// Only the fields of its kind are sent (DiscoverReasonKinds). month = yyyymm, never an exact date; near never carries a place.
public class DiscoverReasonDto
{
    public string Kind { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CityName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Month { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MutualName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MutualCount { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Tags { get; set; }
}

public class DiscoverTripDto
{
    public string CityName { get; set; } = string.Empty;
    public int Month { get; set; }
}

public class DiscoverPageDto
{
    public IReadOnlyList<DiscoverPersonDto> Items { get; set; } = [];
    public string? NextCursor { get; set; }

    // Only for chip=sameTrip when every sameTrip row points to the caller's one soonest trip city.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiscoverTripDto? MyTrip { get; set; }
}

public class DiscoverSimilarDto
{
    public IReadOnlyList<DiscoverPersonDto> Items { get; set; } = [];
}
