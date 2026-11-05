using System.Globalization;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// First row of a platform's update gate on a fresh database. Default: disabled at build 1 (deploying never gates anyone).
// With APP_MIN_BUILD_ANDROID set (the release build that the store serves, above the last Rooms/Plots build 57), the
// Android row starts enabled at that build, latest = that build and MaxSeenBuild = that build (no "build not seen"
// confirm), so the first /config/app-version answer after a DB reset already blocks older builds. Existing rows are
// never touched.
public static class AppVersionSeed
{
    public const string AndroidMinBuildEnv = "APP_MIN_BUILD_ANDROID";

    public static int? ParseMinBuild(string? raw)
        => int.TryParse(raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var build)
           && build >= AppVersionRules.MinBuildNumber && build <= AppVersionRules.MaxBuildNumber
            ? build
            : null;

    public static AppVersionConfig Initial(string platform, int? androidMinBuild, DateTime nowUtc)
    {
        var gated = platform == AppPlatforms.Android && androidMinBuild is not null;
        var build = gated ? androidMinBuild!.Value : 1;
        return new AppVersionConfig
        {
            Id = Guid.NewGuid(),
            Platform = platform,
            IsEnabled = gated,
            MinBuildNumber = build,
            MinVersionName = "1.0.0",
            LatestBuildNumber = build,
            LatestVersionName = "1.0.0",
            StoreUrl = platform == AppPlatforms.Android ? AppPlatforms.AndroidStoreUrl : null,
            ForceCacheMaxAgeHours = AppVersionRules.DefaultForceCacheMaxAgeHours,
            MaxSeenBuild = gated ? build : 0,
            UpdatedAt = nowUtc,
        };
    }
}
