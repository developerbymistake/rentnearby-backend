namespace RentNearBy.Tests.Data;

public class AppVersionSeedTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoConfig_AndroidStartsDisabledAtBuildOne()
    {
        var row = AppVersionSeed.Initial(AppPlatforms.Android, null, Now);

        Assert.False(row.IsEnabled);
        Assert.Equal(1, row.MinBuildNumber);
        Assert.Equal(1, row.LatestBuildNumber);
        Assert.Equal(0, row.MaxSeenBuild);
        Assert.Equal(AppPlatforms.AndroidStoreUrl, row.StoreUrl);
        Assert.Equal(AppVersionRules.DefaultForceCacheMaxAgeHours, row.ForceCacheMaxAgeHours);
    }

    [Fact]
    public void MinBuild58_AndroidStartsGated_NoUnseenConfirm()
    {
        var row = AppVersionSeed.Initial(AppPlatforms.Android, 58, Now);

        Assert.True(row.IsEnabled);
        Assert.Equal(58, row.MinBuildNumber);
        Assert.Equal(58, row.LatestBuildNumber);
        Assert.Equal(58, row.MaxSeenBuild);
        Assert.True(row.MinBuildNumber > 57);
    }

    [Fact]
    public void MinBuild_NeverGatesIos()
    {
        var row = AppVersionSeed.Initial(AppPlatforms.Ios, 58, Now);

        Assert.False(row.IsEnabled);
        Assert.Equal(1, row.MinBuildNumber);
        Assert.Equal(0, row.MaxSeenBuild);
        Assert.Null(row.StoreUrl);
    }

    [Theory]
    [InlineData("58", 58)]
    [InlineData(" 58 ", 58)]
    [InlineData("1", 1)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("57.5", null)]
    [InlineData("abc", null)]
    [InlineData("2100000001", null)]
    public void ParseMinBuild(string? raw, int? expected)
        => Assert.Equal(expected, AppVersionSeed.ParseMinBuild(raw));
}
