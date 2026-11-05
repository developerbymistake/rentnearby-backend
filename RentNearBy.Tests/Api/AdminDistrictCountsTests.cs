namespace RentNearBy.Tests.Api;

public class AdminDistrictCountsTests
{
    private static readonly Guid HamirpurHp = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid HamirpurUp = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid Nainital = Guid.Parse("11111111-0000-0000-0000-000000000003");

    private static readonly Dictionary<Guid, (string Name, string StateName)> Districts = new()
    {
        [HamirpurHp] = ("Hamirpur", "Himachal Pradesh"),
        [HamirpurUp] = ("Hamirpur", "Uttar Pradesh"),
        [Nainital] = ("Nainital", "Uttarakhand"),
    };

    [Fact]
    public void SameNameInTwoStates_TwoRowsKeyedById()
    {
        var rows = AdminDistrictCounts.Build([(HamirpurHp, 1), (HamirpurUp, 1)], Districts);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.DistrictId == HamirpurHp && r.StateName == "Himachal Pradesh" && r.Count == 1);
        Assert.Contains(rows, r => r.DistrictId == HamirpurUp && r.StateName == "Uttar Pradesh" && r.Count == 1);
    }

    [Fact]
    public void LargestFirst_TiesByNameThenState()
    {
        var rows = AdminDistrictCounts.Build([(Nainital, 2), (HamirpurUp, 5), (HamirpurHp, 5)], Districts);

        Assert.Equal(new[] { HamirpurHp, HamirpurUp, Nainital }, rows.Select(r => r.DistrictId));
    }

    [Fact]
    public void DuplicateIds_AreSummed()
    {
        var rows = AdminDistrictCounts.Build([(Nainital, 2), (Nainital, 3)], Districts);

        Assert.Single(rows);
        Assert.Equal(5, rows[0].Count);
    }

    [Fact]
    public void UnknownDistrict_KeptWithEmptyName()
    {
        var unknown = Guid.Parse("11111111-0000-0000-0000-0000000000ff");
        var rows = AdminDistrictCounts.Build([(unknown, 4)], Districts);

        Assert.Equal(unknown, rows[0].DistrictId);
        Assert.Equal(string.Empty, rows[0].DistrictName);
    }

    [Fact]
    public void Empty_IsEmpty()
        => Assert.Empty(AdminDistrictCounts.Build([], Districts));
}
