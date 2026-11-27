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
    // Spec 23.9: last name change by the user (null = never since onboarding; the onboarding name and admin changes do not set it).
    public DateTime? NameChangedAt { get; set; }
    // Spec 25: community guidelines accepted (null = never); the version is compared with SocialLimits.GuidelinesVersion.
    public DateTime? GuidelinesAcceptedAt { get; set; }
    public int GuidelinesVersion { get; set; }
    // Post notify (owner 2026-11-27): the district the app last reported (PUT /social/me/area; id only, never coordinates) and
    // when; the nearby digest skips an area older than 30 days. NotifyNearbyTrips = Settings "Trips near me".
    public Guid? LastDistrictId { get; set; }
    public DateTime? LastDistrictAt { get; set; }
    public bool NotifyNearbyTrips { get; set; } = true;
    // Abuse caps (rolling 7 days, never refunded by deleting a post): when this user's posts notified anyone, and every
    // first publish / re-publish. Only in-window times are kept (a few entries).
    public DateTime[] NotifiedPostTimes { get; set; } = [];
    public DateTime[] PublishTimes { get; set; } = [];
}
