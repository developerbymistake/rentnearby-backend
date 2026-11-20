namespace RentNearBy.Core.Models.Buddy;

// Section "BuddyFeature". Every value has a safe default; nothing is required in appsettings.
public sealed class BuddyFeatureOptions
{
    public const string Section = "BuddyFeature";

    public bool JobsEnabled { get; set; } = true;
    public int JobsTickMinutes { get; set; } = 5;
    public int JobsFirstRunDelayMinutes { get; set; } = 4;
    public int ReasonsCacheHours { get; set; } = 24;
}
