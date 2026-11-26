namespace RentNearBy.Core.Models.Social;

// Field rules and fixed numbers of the social feature.
public static class SocialLimits
{
    public const int AboutMax = 150;
    public const int InterestsMin = 3;
    public const int InterestsMax = 5;
    public const int SkillsMin = 1;
    public const int SkillsMax = 6;
    public const int PlacesMin = 1;
    public const int PlacesMax = 20;
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
    public const int PostCitiesMax = 4;
    public const int PostDaysMax = 11;
    public const int WithinMonthsMax = 6;
    public const int StartDateAheadDays = 365;
    public const int JoinNoteMax = 200;
    public const int JoinRequestsPageMax = 100;
    public const int ChatTextMax = 500;
    public const int ChatPreviewMax = 80;
    public const int MessagesPageDefault = 50;
    public const int MessagesPageMax = 100;
    public const int PageDefault = 20;
    public const int PageMax = 50;
    public const int RailMax = 10;
    public const int NearMeKm = 300;
    public const int FeedQueryMin = 2;
    public const int FeedQueryMax = 40;
    public const int MutualsSample = 3;
    public const int MutualsCountCap = 99;
    public const int ReportNoteMax = 300;
    public const int SnapshotMax = 50;
    public const int SnapshotAround = 15;
    public const int SnapshotNewest = 20;
    public const int SeatsAnyMore = 11;
    public const int DaysTenPlus = 11;
    public const int OwnerInterestsShown = 2;
    public const int JoinNoticePreviewMax = 80;
    public const int ChatPurgeBatch = 5000;
    public const int ChatPurgeBatches = 10;
    public const int IdentityReasonMax = 200;
    public const int NameMax = 100;
    public const int RoomFacesMax = 3;

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
