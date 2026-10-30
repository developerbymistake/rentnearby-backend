namespace RentNearBy.Tests.Roles;

public class AppTabKeysTests
{
    [Theory]
    [InlineData(AppTabKeys.Rooms, UserRoles.RentOwner)]
    [InlineData(AppTabKeys.Plots, UserRoles.PropertyOwner)]
    [InlineData(AppTabKeys.Stays, UserRoles.StayOwner)]
    [InlineData(AppTabKeys.Taxi, UserRoles.TaxiDriver)]
    [InlineData(AppTabKeys.Jobs, null)]
    [InlineData(AppTabKeys.Home, null)]
    [InlineData(AppTabKeys.Profile, null)]
    public void LinkedRoleKey_Tab_Role(string tab, string? expected)
        => Assert.Equal(expected, AppTabKeys.LinkedRoleKey(tab));

    [Theory]
    [InlineData(ListingKinds.Room, AppTabKeys.Rooms)]
    [InlineData(ListingKinds.Plot, AppTabKeys.Plots)]
    [InlineData(ListingKinds.Stay, AppTabKeys.Stays)]
    [InlineData(ListingKinds.Taxi, AppTabKeys.Taxi)]
    [InlineData(null, null)]
    [InlineData("Car", null)]
    public void ForListingKind_Kind_Tab(string? kind, string? expected)
        => Assert.Equal(expected, AppTabKeys.ForListingKind(kind));

    [Fact]
    public void NonDeactivatable_HomeAndProfileOnly()
        => Assert.True(AppTabKeys.NonDeactivatable.SetEquals([AppTabKeys.Home, AppTabKeys.Profile]));

    [Fact]
    public void ApiGated_FiveBusinessTabs()
    {
        Assert.Equal(5, AppTabKeys.ApiGated.Count);
        Assert.True(AppTabKeys.ApiGated.SetEquals([AppTabKeys.Rooms, AppTabKeys.Plots, AppTabKeys.Stays, AppTabKeys.Jobs, AppTabKeys.Taxi]));
    }

    [Fact]
    public void All_SevenTabsAndContainsEveryGatedTab()
    {
        Assert.Equal(7, AppTabKeys.All.Length);
        Assert.All(AppTabKeys.ApiGated, t => Assert.Contains(t, AppTabKeys.All));
    }
}
