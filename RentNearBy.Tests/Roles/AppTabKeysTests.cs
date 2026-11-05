namespace RentNearBy.Tests.Roles;

public class AppTabKeysTests
{
    [Theory]
    [InlineData(AppTabKeys.Stays, UserRoles.StayOwner)]
    [InlineData(AppTabKeys.Taxi, UserRoles.TaxiDriver)]
    [InlineData(AppTabKeys.Jobs, null)]
    [InlineData(AppTabKeys.Home, null)]
    [InlineData(AppTabKeys.Profile, null)]
    public void LinkedRoleKey_Tab_Role(string tab, string? expected)
        => Assert.Equal(expected, AppTabKeys.LinkedRoleKey(tab));

    [Theory]
    [InlineData(ListingKinds.Stay, AppTabKeys.Stays)]
    [InlineData(ListingKinds.Taxi, AppTabKeys.Taxi)]
    [InlineData(null, null)]
    [InlineData("Car", null)]
    [InlineData("Room", null)]
    public void ForListingKind_Kind_Tab(string? kind, string? expected)
        => Assert.Equal(expected, AppTabKeys.ForListingKind(kind));

    [Fact]
    public void NonDeactivatable_HomeAndProfileOnly()
        => Assert.True(AppTabKeys.NonDeactivatable.SetEquals([AppTabKeys.Home, AppTabKeys.Profile]));

    [Fact]
    public void ApiGated_ThreeBusinessTabs()
    {
        Assert.Equal(3, AppTabKeys.ApiGated.Count);
        Assert.True(AppTabKeys.ApiGated.SetEquals([AppTabKeys.Stays, AppTabKeys.Jobs, AppTabKeys.Taxi]));
    }

    [Fact]
    public void All_FiveTabsAndContainsEveryGatedTab()
    {
        Assert.Equal(5, AppTabKeys.All.Length);
        Assert.All(AppTabKeys.ApiGated, t => Assert.Contains(t, AppTabKeys.All));
    }
}
