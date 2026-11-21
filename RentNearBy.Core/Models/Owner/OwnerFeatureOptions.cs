namespace RentNearBy.Core.Models.Owner;

// Section "OwnerFeature" (contract 5.2). Every value has a safe default; nothing is required in appsettings. No secret.
public sealed class OwnerFeatureOptions
{
    public const string Section = "OwnerFeature";

    public string TaxiReminderIst { get; set; } = "07:00";
    public string StayReminderIst { get; set; } = "08:00";
    public string FestivalJobIst { get; set; } = "10:00";
    public string StatsFlushIst { get; set; } = "00:30";
    public int RepairMinutes { get; set; } = 15;
    public int JobBatch { get; set; } = 500;

    // The reminder runs are skipped when they would start this many hours after their time (5.1: 07:00 -> 11:00, 08:00 -> 12:00).
    public int ReminderLateHours { get; set; } = 4;
}
