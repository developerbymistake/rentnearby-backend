namespace RentNearBy.Core.Models.Social;

// Field rules and fixed numbers of the social feature.
public static class SocialLimits
{
    public const int AboutMax = 150;
    // Owner 2026-11-28 (I-10): interests and skills 2..3, places visited 1..3.
    public const int InterestsMin = 2;
    public const int InterestsMax = 3;
    public const int SkillsMin = 2;
    public const int SkillsMax = 3;
    public const int PlacesMin = 1;
    public const int PlacesMax = 3;
    public const int PlaceMinLength = 2;
    public const int PlaceMaxLength = 40;
    public const int HomeCityMax = 80;
    public const int HomeStateMax = 60;
    public const int AdultMonths = 216;
    public const int BirthYearMin = 1900;
    public const int BirthYearMax = 2100;
    public const int FollowingMax = 2000;
    public const int FollowedPostedMax = 2000;
    public const int PostTextMax = 500;
    // A trip's cities shown on its post (a trip posted from Where to next keeps them, up to 4).
    public const int PostCitiesMax = 4;
    // Owner 2026-11-28 (I-6): a new post from Travel buddies has exactly 1 city and lasts 1..4 days.
    public const int PostCreateCitiesMax = 1;
    public const int PostDaysMax = 4;
    public const int WithinMonthsMax = 6;
    public const int StartDateAheadDays = 365;
    public const int JoinNoteMax = 200;
    public const int JoinRequestsPageMax = 100;
    public const int ChatTextMax = 500;
    // Owner 2026-11-28 (I-9): a Free member may send this many messages in each chat room (Direct or trip group).
    public const int FreeMessagesPerRoom = 1;
    public const int ChatPreviewMax = 80;
    public const int MessagesPageDefault = 50;
    public const int MessagesPageMax = 100;
    public const int PageDefault = 20;
    public const int PageMax = 50;
    public const int RailMax = 10;
    public const int NearMeKm = 150;
    public const int FeedQueryMin = 2;
    public const int FeedQueryMax = 40;
    public const int MutualsSample = 3;
    public const int MutualsCountCap = 99;
    public const int ReportNoteMax = 300;
    public const int SnapshotMax = 50;
    public const int SnapshotAround = 15;
    public const int SnapshotNewest = 20;
    public const int SeatsAnyMore = 11;
    public const int OwnerInterestsShown = 2;
    public const int JoinNoticePreviewMax = 80;
    public const int ChatPurgeBatch = 5000;
    public const int ChatPurgeBatches = 10;
    public const int IdentityReasonMax = 200;
    public const int NameMax = 100;
    // Spec 23.9: a user may change his name once every 14 days (like Instagram).
    public const int NameChangeDays = 14;
    public const int RoomFacesMax = 3;
    // Spec 25: bump to show the community guidelines sheet to everyone again.
    public const int GuidelinesVersion = 1;
    // Abuse caps (PostNotifyRules): publishes per rolling 7 days, pending join requests per post, nearby digest reach. The weekly
    // notified-post cap is gone (owner 2026-11-28, I-2).
    public const int PublishesPerWeek = 4;
    public const int PendingRequestsMax = 50;
    public const int NearbyPerPostMax = 50;
    public const int NearbyStartWithinDays = 60;
    // Discover (Buddy_resume/discover/DESIGN.md 1, 3): page size, cached snapshot rows, per-chip quota, X hide days, similar strip.
    public const int DiscoverPage = 20;
    public const int DiscoverSnapshotMax = 200;
    public const int DiscoverChipQuota = 40;
    public const int DiscoverHideDays = 30;
    public const int DiscoverSimilarDefault = 5;
    public const int DiscoverSimilarMax = 10;

    public static readonly TimeSpan MessageLife = TimeSpan.FromHours(24);
    public static readonly TimeSpan RequestFirstMessageLife = TimeSpan.FromDays(7);
    public static readonly TimeSpan AskAgainAfter = TimeSpan.FromDays(30);
    public static readonly TimeSpan DeclinedRoomBlocksFor = TimeSpan.FromDays(30);
    public static readonly TimeSpan SnapshotKeep = TimeSpan.FromDays(90);
    public static readonly TimeSpan JoinRequestsKeep = TimeSpan.FromDays(90);
    public static readonly TimeSpan DirectRoomIdleKeep = TimeSpan.FromDays(90);
    public static readonly TimeSpan DeclinedRoomKeep = TimeSpan.FromDays(30);

    public static readonly IReadOnlyList<int> SeatChoices = [1, 2, 3, SeatsAnyMore];
}
