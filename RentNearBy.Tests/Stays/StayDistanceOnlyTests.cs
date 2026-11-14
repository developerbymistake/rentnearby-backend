using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Utils;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.Stays;

// Stay nearby/nearest without district or city: distance only, nearest capped at StayLimits.NearestMaxKm.
public class StayDistanceOnlyTests
{
    private static readonly GeoCell Cell = GeoCacheKey.Cell(29.3805, 79.4605);
    private static readonly Guid Type = Guid.Parse("e1000000-0000-0000-0000-000000000001");

    private static StayNearbyDto Stay(double lat, double lng, string? thumb = null) => new()
    {
        Id = Guid.NewGuid(), Latitude = (decimal)lat, Longitude = (decimal)lng, ThumbnailUrl = thumb,
    };

    [Fact]
    public void Cap_is_one_hundred_km() => Assert.Equal(100, StayLimits.NearestMaxKm);

    [Fact]
    public void NearestWithin_drops_stays_beyond_the_cap_and_keeps_the_rest()
    {
        var near = Stay(29.40, 79.46);
        var edgeIn = Stay(29.3805 + 99.0 / 111.195, 79.4605);
        var edgeOut = Stay(29.3805 + 101.0 / 111.195, 79.4605);
        var list = StayGeoRefine.NearestWithin([edgeOut, edgeIn, near], 29.3805, 79.4605, 10, StayLimits.NearestMaxKm);
        Assert.Equal(new[] { near.Id, edgeIn.Id }, list.Select(s => s.Id));
    }

    [Fact]
    public void NearestWithin_takes_count_then_applies_the_cap_so_far_stays_never_fill_the_list()
    {
        var far = Stay(29.3805 + 150.0 / 111.195, 79.4605);
        Assert.Empty(StayGeoRefine.NearestWithin([far], 29.3805, 79.4605, 5, StayLimits.NearestMaxKm));
    }

    [Fact]
    public void CentreCap_covers_the_cap_from_every_point_of_the_cell()
    {
        var centreCap = StayGeoRefine.CentreCapKm(Cell, StayLimits.NearestMaxKm);
        Assert.True(centreCap > StayLimits.NearestMaxKm + 2 * GeoCacheKey.HalfDiagonalKm(Cell));
        // a stay exactly at the cap from the cell corner is inside the centre cap
        var corner = (Lat: Cell.CenterLat + Cell.Step / 2, Lng: Cell.CenterLng + Cell.Step / 2);
        var stayLat = corner.Lat + StayLimits.NearestMaxKm / 111.195;
        Assert.True(GeoMath.HaversineKm(Cell.CenterLat, Cell.CenterLng, stayLat, corner.Lng) <= centreCap);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(20)]
    public void Candidate_radius_covers_the_requested_radius(double radius)
        => Assert.True(GeoCacheKey.CandidateRadiusKm(Cell, radius) >= radius);

    [Fact]
    public void Candidate_radius_for_20_is_larger_than_for_12()
        => Assert.True(GeoCacheKey.CandidateRadiusKm(Cell, 20) > GeoCacheKey.CandidateRadiusKm(Cell, 12));

    [Fact]
    public void Keys_have_no_district_and_differ_by_type_radius_count_and_cell()
    {
        var nearby = StayCache.AnyNearbyKey(Type, 12, Cell);
        Assert.StartsWith("stays:nearby:any:", nearby);
        Assert.NotEqual(nearby, StayCache.AnyNearbyKey(null, 12, Cell));
        Assert.NotEqual(nearby, StayCache.AnyNearbyKey(Type, 20, Cell));
        Assert.NotEqual(nearby, StayCache.AnyNearbyKey(Type, 12, GeoCacheKey.Cell(29.5, 79.5)));
        var nearest = StayCache.AnyNearestKey(Type, 5, Cell);
        Assert.StartsWith("stays:nearest:any:", nearest);
        Assert.NotEqual(nearest, StayCache.AnyNearestKey(Type, 6, Cell));
        Assert.NotEqual(nearby, nearest);
    }

    [Fact]
    public void Keys_never_collide_with_the_district_keys()
    {
        var district = Guid.NewGuid();
        Assert.NotEqual(StayCache.NearbyKey(district, null, 12, Cell), StayCache.AnyNearbyKey(null, 12, Cell));
        Assert.NotEqual(StayCache.NearestKey(district, null, 5, Cell), StayCache.AnyNearestKey(null, 5, Cell));
    }

    [Fact]
    public void Ttl_is_sixty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), StayCache.NearbyTtl);
        Assert.Equal(TimeSpan.FromSeconds(60), StayCache.NearestTtl);
    }

    [Fact]
    public void Global_generation_key_is_stays_gen_all_and_bumped_for_stay_changes()
    {
        Assert.Equal("stays:gen:all", ListingCacheKeys.StayAllGen);
        Assert.Contains(ListingCacheKeys.StayAllGen, ListingCacheKeys.GlobalGenKeys(ListingKinds.Stay));
    }

    [Fact]
    public void Map_thumbs_transform_cloudinary_urls_only()
    {
        var items = new List<StayNearbyDto>
        {
            Stay(29.4, 79.4, "https://res.cloudinary.com/demo/image/upload/v1/a.jpg"),
            Stay(29.4, 79.4, "https://example.com/a.jpg"),
            Stay(29.4, 79.4, null),
        };
        StayGeoRefine.WithMapThumbs(items);
        Assert.Equal($"https://res.cloudinary.com/demo/image/upload/{CloudinaryThumb.StayMap}/v1/a.jpg", items[0].ThumbnailUrl);
        Assert.Equal("https://example.com/a.jpg", items[1].ThumbnailUrl);
        Assert.Null(items[2].ThumbnailUrl);
        StayGeoRefine.WithMapThumbs(items);
        Assert.Equal($"https://res.cloudinary.com/demo/image/upload/{CloudinaryThumb.StayMap}/v1/a.jpg", items[0].ThumbnailUrl);
        Assert.Equal("c_fill,w_480,h_360,q_auto,f_auto", CloudinaryThumb.StayMap);
    }

}

// Needs RENTNEARBY_TEST_DB (PostGIS). Written, not run where the variable is unset.
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public class StayRadiusDbTests(TestDatabase db)
{
    private const double Lat = 29.2183, Lng = 79.5130;

    private async Task<Guid> StayAtKmNorthAsync(JourneyHarness h, double km)
    {
        var id = await h.AddStayAsync(await h.AddStayOwnerAsync());
        await using var ctx = db.CreateContext();
        await ctx.StayListings.Where(s => s.Id == id).ExecuteUpdateAsync(x => x
            .SetProperty(s => s.Latitude, (decimal)(Lat + km / 111.195)).SetProperty(s => s.Longitude, (decimal)Lng));
        return id;
    }

    private async Task<HashSet<Guid>> HandlerPathAsync(double radius)
    {
        var cell = GeoCacheKey.Cell(Lat, Lng);
        await using var ctx = db.CreateContext();
        var candidates = (await new StayListingRepository(ctx).GetNearbyAsync(
            cell.CenterLat, cell.CenterLng, GeoCacheKey.CandidateRadiusKm(cell, radius), null, null)).ToList();
        return StayGeoRefine.WithinRadius(candidates, Lat, Lng, radius).Select(s => s.Id).ToHashSet();
    }

    [IntegrationFact]
    public async Task Stay_at_15_km_is_missed_at_12_found_at_20_and_20_is_a_superset_of_12()
    {
        await using var h = new JourneyHarness(db);
        var near = await StayAtKmNorthAsync(h, 5);
        var mid = await StayAtKmNorthAsync(h, 15);
        var r12 = await HandlerPathAsync(12);
        var r20 = await HandlerPathAsync(20);
        Assert.Contains(near, r12);
        Assert.DoesNotContain(mid, r12);
        Assert.Contains(mid, r20);
        Assert.True(r12.IsSubsetOf(r20));
    }

    [IntegrationFact]
    public async Task Stay_at_25_km_is_not_returned_at_20()
    {
        await using var h = new JourneyHarness(db);
        var far = await StayAtKmNorthAsync(h, 25);
        Assert.DoesNotContain(far, await HandlerPathAsync(20));
    }
}
