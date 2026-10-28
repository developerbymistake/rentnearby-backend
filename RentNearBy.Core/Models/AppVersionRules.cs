namespace RentNearBy.Core.Models;

public static class AppVersionRules
{
    // First user-app build (pubspec +N) that ships the update gate; older installs never call the endpoint.
    public const int FirstGatedBuild = 52;

    public const int MinBuildNumber = 1;
    public const int MaxBuildNumber = 2_100_000_000;

    public const int DefaultForceCacheMaxAgeHours = 168;
    public const int MinForceCacheMaxAgeHours = 1;
    public const int MaxForceCacheMaxAgeHours = 720;

    // Caps how far an anonymous caller can push MaxSeenBuild above max(latest, FirstGatedBuild).
    public const int MaxSeenBuildHeadroom = 100;

    public const int MaxVersionNameLength = 32;
    public const int MaxStoreUrlLength = 500;
    public const int MaxMessageLength = 300;
    public const int MaxReasonLength = 300;
}
