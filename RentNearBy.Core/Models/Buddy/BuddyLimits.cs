namespace RentNearBy.Core.Models.Buddy;

// Contract appendix B.
public static class BuddyLimits
{
    public const int FindPerHour = 20;
    public const int FindPerHourPerIp = 60;
    public const int RequestsPerDay = 10;
    public const int MaxWaitingRequests = 20;
    public static readonly TimeSpan RequestExpiry = TimeSpan.FromDays(14);
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromDays(30);
    public static readonly TimeSpan EndedSentShownFor = TimeSpan.FromDays(7);
    public const int MaxBuddies = 500;
    public const int MaxBlocks = 1000;
    public const int BlocksPerHour = 30;
    public const int ReportsPerHour = 5;
    public const int AutoHoldReports = 3;
    public const int AutoHoldReporters = 3;
    public static readonly TimeSpan AutoHoldWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan AutoHoldFor = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaxAdminHold = TimeSpan.FromDays(365);

    public const int MaxTravellers = 12;
    public const int MaxViewers = MaxTravellers - 1;
    public const int InvitesPerDay = 30;
    public static readonly TimeSpan InviteExpiry = TimeSpan.FromDays(14);
    public static readonly TimeSpan ReinviteAfterDecline = TimeSpan.FromDays(7);

    public const int MinAmount = 1;
    public const int MaxAmount = 500_000;
    public const int MaxLiveExpenses = 500;
    public const int MinSharers = 1;
    public const int MaxSharers = 20;
    public const int TitleMax = 60;
    public const int NoteMax = 200;
    public const int ReportNoteMax = 200;
    public const int ShareWithMaxItems = 50;
    public const int FrozenAfterTripEndDays = 30;
    public const int MinPaymentAmount = 1;
    public const int MaxPaymentAmount = 5_000_000;
    public const int RemindersPerPayerPerDay = 1;
    public static readonly IReadOnlyList<int> AutoReminderDaysAfterEnd = [3, 7];

    public const int NoticeRetentionDays = 30;
    public const int EndedRequestRetentionDays = 90;
    public const int EndedInviteRetentionDays = 90;
    public const int EndedPaymentRetentionDays = 90;

    public const int ExpensePushBucketMinutes = 10;
    public static readonly TimeSpan ExpensePushDelay = TimeSpan.FromSeconds(60);
    public const int PlanPushBucketMinutes = 30;
    public static readonly TimeSpan PlanPushDelay = TimeSpan.FromMinutes(10);

    public const int RequestsPageMax = 50;
    public const int BuddiesPageMax = 100;
    public const int BuddiesPageDefault = 50;
    public const int BlocksPageMax = 50;
    public const int SharedTripsPageMax = 20;
    public const int ExpensesPageMax = 200;
    public const int ExpensesPageDefault = 100;
    public const int NoticesPageMax = 50;
    public const int NoticesPageDefault = 30;
    public const int HistoryMax = 50;
    public const int InviteOptionsMax = 50;
    public const int LedgerPaymentsMax = 300;
    public const int JobBatchSize = 1000;
    public const int ReminderJobTripBatch = 200;
    public const int FindQueryMax = 40;
    public const int SeenIdsMax = 50;
    public const int InviteIdsMax = 11;
}
