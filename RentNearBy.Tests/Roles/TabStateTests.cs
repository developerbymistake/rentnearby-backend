using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Roles;

public class TabStateTests
{
    private static List<AppTabDto> Tabs(params (string Key, bool Active)[] tabs)
        => tabs.Select(t => new AppTabDto { TabKey = t.Key, IsActive = t.Active }).ToList();

    [Fact]
    public void IsActive_NoRows_FailOpen()
        => Assert.True(TabState.IsActive(Tabs(), AppTabKeys.Rooms));

    [Fact]
    public void IsActive_InactiveRow_False()
        => Assert.False(TabState.IsActive(Tabs((AppTabKeys.Rooms, false)), AppTabKeys.Rooms));

    [Fact]
    public void IsActive_ActiveRow_True()
        => Assert.True(TabState.IsActive(Tabs((AppTabKeys.Rooms, true), (AppTabKeys.Plots, false)), AppTabKeys.Rooms));

    [Fact]
    public void IsActive_NullKey_True()
        => Assert.True(TabState.IsActive(Tabs((AppTabKeys.Rooms, false)), null));

    [Fact]
    public void AllActive_OneOff_False()
        => Assert.False(TabState.AllActive(Tabs((AppTabKeys.Jobs, true), (AppTabKeys.Stays, false)), [AppTabKeys.Jobs, AppTabKeys.Stays]));

    [Fact]
    public void AllActive_AllOnOrMissing_True()
        => Assert.True(TabState.AllActive(Tabs((AppTabKeys.Jobs, true)), [AppTabKeys.Jobs, AppTabKeys.Stays]));

    [Fact]
    public void IsKindActive_TaxiOff_False()
        => Assert.False(TabState.IsKindActive(Tabs((AppTabKeys.Taxi, false)), ListingKinds.Taxi));

    [Fact]
    public void IsKindActive_NullKind_True()
        => Assert.True(TabState.IsKindActive(Tabs((AppTabKeys.Taxi, false)), null));

    [Fact]
    public void IsRoleAvailable_InactiveRole_False()
    {
        var role = new Role { Id = UserRoles.RentOwnerId, Key = UserRoles.RentOwner, IsActive = false };

        Assert.False(TabState.IsRoleAvailable(Tabs((AppTabKeys.Rooms, true)), role));
    }

    [Fact]
    public void IsRoleAvailable_ActiveRoleTabOff_False()
    {
        var role = new Role { Id = UserRoles.RentOwnerId, Key = UserRoles.RentOwner, IsActive = true };

        Assert.False(TabState.IsRoleAvailable(Tabs((AppTabKeys.Rooms, false)), role));
    }

    [Fact]
    public void IsRoleAvailable_SeekerWithEveryTabOff_True()
    {
        var role = new Role { Id = UserRoles.SeekerId, Key = UserRoles.Seeker, IsActive = true };

        Assert.True(TabState.IsRoleAvailable(Tabs((AppTabKeys.Rooms, false), (AppTabKeys.Plots, false)), role));
    }
}
