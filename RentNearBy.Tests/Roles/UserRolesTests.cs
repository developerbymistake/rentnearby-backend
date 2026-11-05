namespace RentNearBy.Tests.Roles;

public class UserRolesTests
{
    public static TheoryData<Guid, string, string?, string?> Roles => new()
    {
        { UserRoles.SeekerId, UserRoles.Seeker, null, null },
        { UserRoles.StayOwnerId, UserRoles.StayOwner, ListingKinds.Stay, AppTabKeys.Stays },
        { UserRoles.TaxiDriverId, UserRoles.TaxiDriver, ListingKinds.Taxi, AppTabKeys.Taxi },
    };

    [Theory]
    [MemberData(nameof(Roles))]
    public void KnownRole_KeyKindAndTab(Guid roleId, string key, string? kind, string? tab)
    {
        Assert.Equal(key, UserRoles.KeyOf(roleId));
        Assert.Equal(kind, UserRoles.ListingKindOf(roleId));
        Assert.Equal(tab, UserRoles.TabKeyOf(roleId));
    }

    [Fact]
    public void FixedIds_MatchSeededRows()
    {
        Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000001"), UserRoles.SeekerId);
        Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000004"), UserRoles.StayOwnerId);
        Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000005"), UserRoles.TaxiDriverId);
    }

    [Theory]
    [InlineData("10000000-0000-0000-0000-000000000002")]
    [InlineData("10000000-0000-0000-0000-000000000003")]
    public void RetiredRoleIds_AreUnknown(string id)
    {
        var roleId = Guid.Parse(id);
        Assert.Throws<InvalidOperationException>(() => UserRoles.KeyOf(roleId));
        Assert.Throws<InvalidOperationException>(() => UserRoles.ListingKindOf(roleId));
    }

    [Fact]
    public void KeyOf_UnknownRole_Throws()
        => Assert.Throws<InvalidOperationException>(() => UserRoles.KeyOf(Guid.NewGuid()));

    [Fact]
    public void ListingKindOf_UnknownRole_Throws()
        => Assert.Throws<InvalidOperationException>(() => UserRoles.ListingKindOf(Guid.NewGuid()));

    [Fact]
    public void TabKeyOf_UnknownRole_Throws()
        => Assert.Throws<InvalidOperationException>(() => UserRoles.TabKeyOf(Guid.Empty));
}
