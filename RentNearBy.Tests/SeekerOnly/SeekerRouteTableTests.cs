using static RentNearBy.Tests.SeekerOnly.SeekerRouteTable;

namespace RentNearBy.Tests.SeekerOnly;

// BE-T1 (contract section 1): the built route table, route by route. A new /api/v1 route that is in no list fails, so a
// traveller route cannot ship without the SeekerOnly policy and an owner route cannot be gated by mistake.
public class SeekerRouteTableTests
{
    private static readonly (string Name, string[] Keys)[] Lists =
    [
        (nameof(Seeker), Seeker), (nameof(OpenUser), OpenUser), (nameof(OwnerStay), OwnerStay), (nameof(OwnerTaxi), OwnerTaxi),
        (nameof(SeekerRouteTable.Admin), SeekerRouteTable.Admin), (nameof(Anonymous), Anonymous),
    ];

    [Fact]
    public void Every_api_route_is_in_exactly_one_list_and_every_listed_route_exists()
    {
        var listed = Lists.SelectMany(l => l.Keys.Select(k => (l.Name, Key: k))).ToList();
        foreach (var group in listed.GroupBy(x => x.Key))
            Assert.True(group.Count() == 1, $"{group.Key} is listed in {string.Join(", ", group.Select(g => g.Name))}");

        var unlisted = Routes.Keys.Except(listed.Select(x => x.Key)).OrderBy(k => k).ToList();
        Assert.True(unlisted.Count == 0, "Unlisted routes: " + string.Join("; ", unlisted));
        var stale = listed.Select(x => x.Key).Except(Routes.Keys).OrderBy(k => k).ToList();
        Assert.True(stale.Count == 0, "Listed routes that are not mapped: " + string.Join("; ", stale));
    }

    [Fact]
    public void Gate_total_is_123()
    {
        Assert.Equal(123, Seeker.Length);
        Assert.Equal(123, Seeker.Distinct().Count());
        Assert.Equal(123, Routes.Values.Count(r => r.Policies.Contains(AuthPolicies.SeekerOnly)));
    }

    [Fact]
    public void Seeker_routes_carry_exactly_the_seeker_policy()
    {
        foreach (var key in Seeker)
        {
            var r = Route(key);
            Assert.True(r.Policies.SequenceEqual([AuthPolicies.SeekerOnly]), $"{key}: [{string.Join(", ", r.Policies)}]");
            Assert.False(r.AllowAnonymous, key);
        }
    }

    [Fact]
    public void Open_user_routes_need_a_signed_in_user_and_no_policy()
    {
        foreach (var key in OpenUser)
        {
            var r = Route(key);
            Assert.NotEmpty(r.Authorize);
            Assert.True(r.Policies.All(p => p == null), $"{key}: [{string.Join(", ", r.Policies)}]");
            Assert.False(r.AllowAnonymous, key);
        }
    }

    [Theory]
    [InlineData(nameof(OwnerStay), "CanListStays")]
    [InlineData(nameof(OwnerTaxi), "CanListTaxis")]
    [InlineData(nameof(SeekerRouteTable.Admin), "AdminOnly")]
    public void Owner_and_admin_routes_keep_their_policy(string list, string policy)
    {
        foreach (var key in Lists.Single(l => l.Name == list).Keys)
        {
            var r = Route(key);
            Assert.NotEmpty(r.Authorize);
            Assert.True(r.Policies.All(p => p == policy), $"{key}: [{string.Join(", ", r.Policies)}]");
            Assert.False(r.AllowAnonymous, key);
        }
    }

    [Fact]
    public void Anonymous_routes_need_no_token()
    {
        foreach (var key in Anonymous)
        {
            var r = Route(key);
            Assert.True(r.AllowAnonymous || r.Authorize.Count == 0, key);
        }
    }

    // M1: the stay map place page reads one Atlas spot for every role; the spot list, minutes, supply and plan stay gated.
    [Fact]
    public void Spot_detail_is_open_and_the_planner_reads_are_gated()
    {
        Assert.Contains("GET /api/v1/trips/cities/{cityKey:long}/spots/{spotId}", OpenUser);
        Assert.Contains("GET /api/v1/trips/cities/{cityKey:long}/spots", Seeker);
        Assert.Contains("GET /api/v1/trips/cities", OpenUser);
        Assert.Contains("GET /api/v1/trips/cities/version", OpenUser);
        Assert.Contains("GET /api/v1/trips/cities/nearest", OpenUser);
    }
}
