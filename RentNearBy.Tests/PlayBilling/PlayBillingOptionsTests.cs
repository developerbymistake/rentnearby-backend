namespace RentNearBy.Tests.PlayBilling;

public class PlayBillingOptionsTests
{
    [Theory]
    [InlineData("com.bakhli.app", "{}", null, 32, true)]
    [InlineData("com.bakhli.app", "{}", null, 64, true)]
    [InlineData("com.bakhli.app", "{}", null, 31, false)]
    [InlineData("com.bakhli.app", "", "/run/secrets/play.json", 32, true)]
    [InlineData("com.bakhli.app", null, "/run/secrets/play.json", 32, true)]
    [InlineData("", "{}", null, 32, false)]
    [InlineData("com.bakhli.app", "", "", 32, false)]
    [InlineData("com.bakhli.app", null, null, 32, false)]
    public void IsConfigured_PackageCredentialsAndSalt(string package, string? json, string? path, int saltLength, bool expected)
    {
        var options = new PlayBillingOptions
        {
            PackageName = package,
            ServiceAccountJson = json,
            ServiceAccountJsonPath = path,
            AccountIdSalt = new string('s', saltLength),
        };

        Assert.Equal(expected, options.IsConfigured);
    }

    [Fact]
    public void Defaults_NotConfiguredAndCancelAction()
    {
        var options = new PlayBillingOptions();

        Assert.False(options.IsConfigured);
        Assert.Equal(PlayServerActions.Cancel, options.RoleChangeAction);
        Assert.Equal("PlayBilling", PlayBillingOptions.Section);
    }

    [Fact]
    public void IsConfigured_NullSalt_False()
    {
        var options = new PlayBillingOptions { PackageName = "com.bakhli.app", ServiceAccountJson = "{}", AccountIdSalt = null! };

        Assert.False(options.IsConfigured);
    }
}
