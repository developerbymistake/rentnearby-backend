namespace RentNearBy.Core.Entities.Social;

// Table "UserProfiles": one row per seeker who finished the social onboarding (row exists = profile completed).
// Gender and BirthMonth/BirthYear are written once; only the admin identity route changes them (ProfileIdentityAudit).
public class UserProfile
{
    public Guid UserId { get; set; }
    public string Gender { get; set; } = string.Empty;
    public short BirthMonth { get; set; }
    public short BirthYear { get; set; }
    public string About { get; set; } = string.Empty;
    public string[] Interests { get; set; } = [];
    public string[] Skills { get; set; } = [];
    public string[] Places { get; set; } = [];
    public string? HomeCityName { get; set; }
    public string? HomeStateName { get; set; }
    public bool IsPrivate { get; set; }
    public string MessagePolicy { get; set; } = "Everyone";
    public bool NotifyMessages { get; set; } = true;
    public bool NotifyFollows { get; set; } = true;
    public bool NotifyFollowedTrips { get; set; } = true;
    public int FollowerCount { get; set; }
    public int FollowingCount { get; set; }
    public DateTime CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
