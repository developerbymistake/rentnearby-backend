namespace RentNearBy.Core.DTOs.Social;

// Contract 2.1 Person. photoUrl is always the 160 face thumb. Never a phone, email or birth field.
public class SocialPersonDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? PhotoUrl { get; set; }
    public string Initial { get; set; } = string.Empty;
    public string? HomeCity { get; set; }
    // Male / Female / Other (the profile gender, already public on the traveller profile); drives the row's gender glyph.
    public string? Gender { get; set; }
}

public class PersonRowDto : SocialPersonDto
{
    public bool FollowsMe { get; set; }
    public bool IFollow { get; set; }
    public bool FollowRequested { get; set; }
    public bool IsBuddy { get; set; }
    public int MutualCount { get; set; }
    public string? MutualName { get; set; }
}

public class FollowRequestRowDto : PersonRowDto
{
    public DateTime RequestedAt { get; set; }
}

public class MutualsDto
{
    public int Count { get; set; }
    public List<string> Names { get; set; } = [];
    public List<string> PhotoUrls { get; set; } = [];
}

public class LimitUseDto
{
    public int Limit { get; set; }
    public int Used { get; set; }
}

public class SocialLimitsDto
{
    public bool IsPlus { get; set; }
    public bool CanUpgrade { get; set; }
    public DateTime ResetsAt { get; set; }
    public LimitUseDto ActiveTrips { get; set; } = new();
    public LimitUseDto PublicTrips { get; set; } = new();
    public LimitUseDto JoinRequests { get; set; } = new();
    public LimitUseDto NewChats { get; set; } = new();
    public bool GenderOnlyTrips { get; set; }
    public bool NearMe { get; set; }
    // PLAN_STATES_SOCIAL 2: trips kept inactive, and posts paused on trips that stay active, after Plus ended (both come back
    // with Plus). The two never count the same trip.
    public int InactiveTrips { get; set; }
    public int PausedPosts { get; set; }
}

public class ChatUnreadDto
{
    public int People { get; set; }
    public int Trips { get; set; }
    public int Requests { get; set; }
    public int Total { get; set; }
}

public class MyCountersDto
{
    public int Trips { get; set; }
    public int Followers { get; set; }
    public int Following { get; set; }
    public int Buddies { get; set; }
}

public class TravellerCountersDto
{
    public int Trips { get; set; }
    public int Followers { get; set; }
    public int Following { get; set; }
}

// Own profile only: the one DTO that carries birth month and year (never shown to others).
public class MyProfileDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    // Spec 23.9: when the name may change again (UTC); null = it may change now. Edit profile locks the field until then.
    public DateTime? NameChangeAllowedAt { get; set; }
    public string? Username { get; set; }
    public string? PhotoUrl { get; set; }
    public string Initial { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public int BirthMonth { get; set; }
    public int BirthYear { get; set; }
    public string? HomeCity { get; set; }
    public string? HomeState { get; set; }
    public string About { get; set; } = string.Empty;
    public List<string> Interests { get; set; } = [];
    public List<string> Skills { get; set; } = [];
    public List<string> Places { get; set; } = [];
    public bool IsPrivate { get; set; }
    public string MessagePolicy { get; set; } = string.Empty;
    public bool NotifyMessages { get; set; }
    public bool NotifyFollows { get; set; }
    public bool NotifyFollowedTrips { get; set; }
    public MyCountersDto Counters { get; set; } = new();
    public List<PublicTripDto> PublicTrips { get; set; } = [];
}

public class TravellerRelationDto
{
    public bool IFollow { get; set; }
    public bool FollowRequested { get; set; }
    public bool FollowsMe { get; set; }
    public bool IsBuddy { get; set; }
    public bool CanAddBuddy { get; set; }
    public bool CanMessage { get; set; }
}

public class TravellerProfileDto
{
    public SocialPersonDto Person { get; set; } = new();
    public string Gender { get; set; } = string.Empty;
    public string About { get; set; } = string.Empty;
    public List<string> Interests { get; set; } = [];
    public List<string> Skills { get; set; } = [];
    public List<string> Places { get; set; } = [];
    public TravellerCountersDto Counters { get; set; } = new();
    public TravellerRelationDto Relation { get; set; } = new();
    public MutualsDto? Mutuals { get; set; }
    public bool PrivateLocked { get; set; }
    public List<PublicTripDto> PublicTrips { get; set; } = [];
}

public class SocialMeCountsDto
{
    public int FollowRequests { get; set; }
    public ChatUnreadDto ChatUnread { get; set; } = new();
    // People I blocked (Settings "Blocked people" line).
    public int Blocked { get; set; }
}

public class SocialCatalogDto
{
    public IReadOnlyList<string> Interests { get; set; } = [];
    public IReadOnlyList<string> Skills { get; set; } = [];
    public IReadOnlyList<string> AboutExamples { get; set; } = [];
    public IReadOnlyList<string> PostChips { get; set; } = [];
}

public class SocialMeDto
{
    // Spec 25: true only when the current version of the community guidelines was accepted.
    public bool GuidelinesAccepted { get; set; }
    public MyProfileDto? Profile { get; set; }
    public SocialLimitsDto Limits { get; set; } = new();
    public SocialMeCountsDto Counts { get; set; } = new();
    public SocialCatalogDto Catalog { get; set; } = new();
}

public class SocialOnboardingRequest
{
    public string? Gender { get; set; }
    public int? BirthMonth { get; set; }
    public int? BirthYear { get; set; }
    public List<string>? Interests { get; set; }
    public List<string>? Skills { get; set; }
    public List<string>? Places { get; set; }
    public string? About { get; set; }
    public string? HomeCityName { get; set; }
    public string? HomeStateName { get; set; }
}

// Absent = keep. Gender / birth present = 409 IdentityLocked. A different name inside 14 days of the last change = 409 NameChangeTooSoon.
public class UpdateSocialProfileRequest
{
    public string? Name { get; set; }
    public string? About { get; set; }
    public List<string>? Interests { get; set; }
    public List<string>? Skills { get; set; }
    public List<string>? Places { get; set; }
    public string? HomeCityName { get; set; }
    public string? HomeStateName { get; set; }
    public string? Gender { get; set; }
    public int? BirthMonth { get; set; }
    public int? BirthYear { get; set; }
}

public class UpdatePrivacyRequest
{
    public bool? IsPrivate { get; set; }
    public string? MessagePolicy { get; set; }
    public bool? NotifyMessages { get; set; }
    public bool? NotifyFollows { get; set; }
    public bool? NotifyFollowedTrips { get; set; }
}

public class AdminIdentityRequest
{
    // Spec 23.9: the admin may change the name at any time; it never starts the user's 14-day name lock.
    public string? Name { get; set; }
    public string? Gender { get; set; }
    public int? BirthMonth { get; set; }
    public int? BirthYear { get; set; }
    public string Reason { get; set; } = string.Empty;
}

// Admin-only answer of PUT /admin/users/{id}/identity.
public class AdminIdentityDto
{
    public string Name { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public int BirthMonth { get; set; }
    public int BirthYear { get; set; }
}
