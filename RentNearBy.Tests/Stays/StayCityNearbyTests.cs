using Microsoft.AspNetCore.Http;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Stays;

public class StayCityNearbyTests
{
    private static readonly System.Security.Claims.ClaimsPrincipal Anonymous = new();

    private static Task<IResult> Nearby(long cityKey, ICityResolver resolver,
        IUnitOfWork uow, double? lat = null, double? lng = null)
        => StayHandlers.GetNearby(lat, lng, null, null, null, cityKey, uow, resolver, Anonymous, CityFixture.NoRedis());

    private static List<Guid> Ids(System.Text.Json.JsonElement data) =>
        data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("Id").GetGuid()).ToList();

    [Fact]
    public async Task A_kathgodam_stay_is_never_listed_under_haldwani_and_the_other_way_round()
    {
        var (uow, stays, _) = CityFixture.Uow();
        var h = CityFixture.Stay(CityFixture.NearHaldwani);
        var k = CityFixture.Stay(CityFixture.NearKathgodam);
        stays.All = [h, k];
        var r = CityFixture.Resolver(CityFixture.Haldwani, CityFixture.Kathgodam);

        var inH = CityFixture.Read(await Nearby(CityFixture.Haldwani.CityKey, r, uow));
        var inK = CityFixture.Read(await Nearby(CityFixture.Kathgodam.CityKey, r, uow));

        Assert.Equal([h.Id], Ids(inH.Data));
        Assert.Equal([k.Id], Ids(inK.Data));
    }

    [Fact]
    public async Task City_list_has_no_district_filter_and_no_radius()
    {
        var (uow, stays, _) = CityFixture.Uow();
        stays.All = [CityFixture.Stay(CityFixture.NearHaldwani)];

        var result = CityFixture.Read(await Nearby(CityFixture.Haldwani.CityKey, CityFixture.Resolver(CityFixture.Haldwani), uow));

        Assert.Equal(200, result.Status);
        Assert.Null(stays.SeenDistrict);
        Assert.Single(Ids(result.Data));
    }

    [Fact]
    public async Task Unknown_city_key_gives_empty_items_and_atlas_down_gives_503()
    {
        var (uow, stays, _) = CityFixture.Uow();
        stays.All = [CityFixture.Stay(CityFixture.NearHaldwani)];

        var unknown = CityFixture.Read(await Nearby(999, CityFixture.Resolver(CityFixture.Haldwani), uow));
        var down = CityFixture.Read(await Nearby(CityFixture.Haldwani.CityKey, CityFixture.NoSnapshot(), uow));

        Assert.Equal(200, unknown.Status);
        Assert.Empty(Ids(unknown.Data));
        Assert.Equal(503, down.Status);
        Assert.Equal(TripErrorTypes.CitiesUnavailable, down.ErrorType);
    }

    [Fact]
    public async Task Pin_only_sorts_paid_rank_first_then_nearest()
    {
        var (uow, stays, _) = CityFixture.Uow();
        var near = CityFixture.Stay((29.2185, 79.5131));
        var far = CityFixture.Stay((29.2400, 79.5200));
        var paid = CityFixture.Stay((29.2400, 79.5200), rank: 3);
        stays.All = [far, near, paid];
        var r = CityFixture.Resolver(CityFixture.Haldwani);

        var result = CityFixture.Read(await Nearby(CityFixture.Haldwani.CityKey, r, uow, CityFixture.Haldwani.Latitude, CityFixture.Haldwani.Longitude));

        Assert.Equal([paid.Id, near.Id, far.Id], Ids(result.Data));
    }

    [Fact]
    public void Order_gives_each_caller_its_own_distance_and_limits_to_the_count()
    {
        var a = CityFixture.Stay(CityFixture.NearHaldwani);
        var b = CityFixture.Stay((29.2400, 79.5200));

        var list = StayCityRank.Order([a, b], CityFixture.NearHaldwani.Lat, CityFixture.NearHaldwani.Lng, 1);

        var only = Assert.Single(list);
        Assert.Equal(a.Id, only.Id);
        Assert.True(only.DistanceKm < 0.5);
    }

    [Fact]
    public async Task Nearest_with_a_city_key_returns_the_count_nearest_inside_the_city()
    {
        var (uow, stays, _) = CityFixture.Uow();
        var near = CityFixture.Stay((29.2185, 79.5131));
        var far = CityFixture.Stay((29.2400, 79.5200));
        var otherCity = CityFixture.Stay(CityFixture.NearKathgodam);
        stays.All = [far, near, otherCity];
        var r = CityFixture.Resolver(CityFixture.Haldwani, CityFixture.Kathgodam);

        var result = CityFixture.Read(await StayHandlers.GetNearest(
            CityFixture.Haldwani.Latitude, CityFixture.Haldwani.Longitude, null, null, CityFixture.Haldwani.CityKey,
            uow, r, Anonymous, CityFixture.NoRedis(), new CityFixture.AllowAll(), new DefaultHttpContext(), count: 1));

        Assert.Equal([near.Id], Ids(result.Data));
    }

    [Fact]
    public async Task Old_district_shape_still_needs_radius_district_and_coordinates()
    {
        var (uow, stays, _) = CityFixture.Uow();
        var r = CityFixture.Resolver(CityFixture.Haldwani);

        var noRadius = await StayHandlers.GetNearby(29.2, 79.5, null, Guid.NewGuid(), null, null, uow, r, Anonymous, CityFixture.NoRedis());
        var noDistrict = await StayHandlers.GetNearby(29.2, 79.5, 5, null, null, null, uow, r, Anonymous, CityFixture.NoRedis());
        var noPin = await StayHandlers.GetNearby(null, null, 5, Guid.NewGuid(), null, null, uow, r, Anonymous, CityFixture.NoRedis());

        Assert.Equal(400, CityFixture.Read(noRadius).Status);
        Assert.Equal(400, CityFixture.Read(noDistrict).Status);
        Assert.Equal(400, CityFixture.Read(noPin).Status);
        Assert.Equal(0, stays.Calls);
    }

    [Fact]
    public void City_cache_key_holds_the_city_and_stay_type_not_a_coordinate_cell()
    {
        var type = Guid.NewGuid();

        Assert.Equal($"stays:city:210000001:{type:N}", StayCache.CityKey(210000001, type));
        Assert.NotEqual(StayCache.CityKey(210000001, null), StayCache.CityKey(210000002, null));
        Assert.DoesNotContain("radius", StayCache.CityKey(210000001, null));
    }
}
