using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Taxis;

public class TaxiRouteFeedTierTests
{
    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130), Nainital = (29.3919, 79.4542), Bhimtal = (29.3470, 79.5640),
        Ramnagar = (29.3950, 79.1280), Ramgarh = (29.4167, 79.5667), Delhi = (28.6139, 77.2090), Dehradun = (30.3165, 78.0322),
        Mussoorie = (30.4598, 78.0644), Village = (29.1000, 79.6000);

    private static (double, double, double, double) R((double Lat, double Lng) a, (double Lat, double Lng) b) => (a.Lat, a.Lng, b.Lat, b.Lng);
    private static FDriver D(string n, string city, (double Lat, double Lng) p, params (double, double, double, double)[] routes) =>
        new(n, city, p.Lat, p.Lng, routes);

    private static Task<TaxiRouteFeedDto> Run(IReadOnlyList<FDriver> ds, (double Lat, double Lng) from, (double Lat, double Lng) to,
        int size = 5, string? tier = null, string? token = null, Func<Task<TaxiWaysResultDto?>>? ways = null) =>
        TaxiFeedComposer.RouteAsync(
            new TaxiRouteFeedArgs(from.Lat, from.Lng, to.Lat, to.Lng, null, size, tier, token),
            TaxiFeedFixture.OnRoute(ds, from.Lat, from.Lng, to.Lat, to.Lng), ways ?? TaxiFeedFixture.NoWays(),
            new FakeFeedQuery(ds), new FakeFeedContext(), TaxiFeedFixture.Hydrate(ds));

    private static TaxiTierDto Tier(TaxiRouteFeedDto d, string key) => d.Tiers.Single(t => t.Key == key);
    private static List<Guid> AllIds(TaxiRouteFeedDto d) => d.Tiers.SelectMany(t => t.Items.Select(i => i.Id)).ToList();

    [Fact]
    public async Task Always_four_tiers_in_order()
    {
        var d = await Run([D("A", "Haldwani", Haldwani)], Haldwani, Nainital);
        Assert.Equal(["onRoute", "passing", "basedNearFrom", "inTo"], d.Tiers.Select(t => t.Key));
        Assert.Equal(["driversOnRoute", "driversPassing", "basedNearFrom", "taxisInTo"], d.Tiers.Select(t => t.HeaderKey));
        Assert.Null(Tier(d, "onRoute").Header);
        Assert.NotNull(Tier(d, "basedNearFrom").Header);
        Assert.NotNull(Tier(d, "inTo").Header);
    }

    // Owner example 1: Haldwani to Nainital, nobody based in Nainital.
    [Fact]
    public async Task Haldwani_to_nainital_without_a_nainital_taxi_still_lists_drivers_with_honest_labels()
    {
        var h = D("Haldwani driver", "Haldwani", Haldwani, R(Haldwani, Nainital));
        var b = D("Bhimtal driver", "Bhimtal", Bhimtal, R(Bhimtal, Delhi));
        var rn = D("Ramnagar driver", "Ramnagar", Ramnagar, R(Ramnagar, Delhi));
        var rg = D("Ramgarh driver", "Ramgarh", Ramgarh, R(Ramgarh, Delhi));
        var ds = new[] { h, b, rn, rg };

        var d = await Run(ds, Haldwani, Nainital, size: 1);

        Assert.Equal([h.Id], Tier(d, "onRoute").Items.Select(i => i.Id));
        Assert.All(Tier(d, "onRoute").Items, i => Assert.True(i.ListedForRoute));
        foreach (var x in new[] { b, rn, rg })
            Assert.DoesNotContain(x.Id, Tier(d, "onRoute").Items.Select(i => i.Id));
        var near = Tier(d, "basedNearFrom").Items.Concat(Tier(d, "inTo").Items).ToList();
        Assert.NotEmpty(Tier(d, "basedNearFrom").Items);
        Assert.NotEmpty(Tier(d, "inTo").Items);
        Assert.All(near, i => { Assert.False(i.ListedForRoute); Assert.Null(i.MatchedFare); Assert.NotNull(i.DistanceKm); });
        Assert.Equal(AllIds(d).Count, AllIds(d).Distinct().Count());
    }

    [Fact]
    public async Task Bhimtal_ramnagar_ramgarh_are_never_on_this_route_even_with_a_big_page()
    {
        var h = D("H", "Haldwani", Haldwani, R(Haldwani, Nainital));
        var others = new[]
        {
            D("Bhimtal", "Bhimtal", Bhimtal, R(Bhimtal, Delhi)), D("Ramnagar", "Ramnagar", Ramnagar, R(Ramnagar, Delhi)),
            D("Ramgarh", "Ramgarh", Ramgarh, R(Ramgarh, Delhi)),
        };
        var d = await Run([h, .. others], Haldwani, Nainital, size: 20);
        var onRoute = Tier(d, "onRoute").Items.Select(i => i.Id).ToList();
        Assert.Equal([h.Id], onRoute);
        var based = Tier(d, "basedNearFrom").Items;
        Assert.Equal(others.Select(o => o.Id).OrderBy(x => x), based.Select(i => i.Id).OrderBy(x => x));
        Assert.All(based, i => Assert.False(i.ListedForRoute));
    }

    // Owner example 2: a village pin to Delhi.
    [Fact]
    public async Task Village_to_delhi_shows_based_nearby_and_taxis_in_delhi_never_empty()
    {
        var h = D("Haldwani driver", "Haldwani", Haldwani, R(Haldwani, Delhi));
        var dl = D("Delhi driver", "Delhi", Delhi, R(Delhi, Dehradun));
        var d = await Run([h, dl], Village, Delhi, size: 1);

        Assert.Empty(Tier(d, "onRoute").Items);
        var from = Tier(d, "basedNearFrom").Items.Single();
        Assert.Equal(h.Id, from.Id);
        Assert.False(from.ListedForRoute);
        Assert.Equal("inCity", from.LabelKey);
        var to = Tier(d, "inTo").Items.Single();
        Assert.Equal(dl.Id, to.Id);
        Assert.Equal("inCity", to.LabelKey);
        Assert.Equal("Delhi", Tier(d, "inTo").Header!.CentreCityName);
    }

    // Owner example 3: Delhi to Mussoorie, no taxi in Mussoorie.
    [Fact]
    public async Task Delhi_to_mussoorie_lists_the_nearest_taxis_to_mussoorie_with_a_far_label()
    {
        var dl = D("Delhi driver", "Delhi", Delhi, R(Delhi, Haldwani));
        var dd = D("Dehradun driver", "Dehradun", Dehradun, R(Dehradun, Delhi));
        var d = await Run([dl, dd], Delhi, Mussoorie, size: 1);

        Assert.Empty(Tier(d, "onRoute").Items);
        Assert.Equal([dl.Id], Tier(d, "basedNearFrom").Items.Select(i => i.Id));
        var to = Tier(d, "inTo").Items.Single();
        Assert.Equal(dd.Id, to.Id);
        Assert.Equal("far", to.LabelKey);
        Assert.False(to.IsNear);
        Assert.True(to.DistanceKm > 15 && to.DistanceKm % 5 == 0);
        Assert.False(to.ListedForRoute);
    }

    [Fact]
    public async Task Whole_result_is_empty_only_when_no_taxi_exists_at_all()
    {
        var d = await Run([], Delhi, Mussoorie);
        Assert.All(d.Tiers, t => Assert.Empty(t.Items));
        Assert.Equal(4, d.Tiers.Count);
    }

    [Fact]
    public async Task A_driver_on_the_route_is_not_repeated_in_passing_or_the_distance_tiers()
    {
        var h = D("H", "Haldwani", Haldwani, R(Haldwani, Nainital));
        var other = D("Other", "Bhimtal", Bhimtal);
        var d = await Run([h, other], Haldwani, Nainital, ways: TaxiFeedFixture.Ways(h, other));
        Assert.Equal([h.Id], Tier(d, "onRoute").Items.Select(i => i.Id));
        Assert.Equal([other.Id], Tier(d, "passing").Items.Select(i => i.Id));
        Assert.All(Tier(d, "passing").Items, i => Assert.True(i.ListedForRoute));
        Assert.Equal(AllIds(d).Count, AllIds(d).Distinct().Count());
        Assert.Empty(Tier(d, "basedNearFrom").Items);
    }

    [Fact]
    public async Task Engine_null_gives_an_empty_passing_tier_not_an_error()
    {
        var d = await Run([D("A", "Haldwani", Haldwani)], Haldwani, Nainital, ways: TaxiFeedFixture.NoWays());
        Assert.Empty(Tier(d, "passing").Items);
        Assert.NotEmpty(Tier(d, "basedNearFrom").Items);
    }

    [Fact]
    public async Task Engine_that_throws_gives_an_empty_passing_tier()
    {
        var d = await Run([D("A", "Haldwani", Haldwani)], Haldwani, Nainital, ways: () => throw new TaxiWaysUnavailableException());
        Assert.Empty(Tier(d, "passing").Items);
        d = await Run([D("A", "Haldwani", Haldwani)], Haldwani, Nainital, ways: () => throw new HttpRequestException("atlas down"));
        Assert.Empty(Tier(d, "passing").Items);
    }

    [Fact]
    public async Task Distance_tiers_collapse_only_when_three_or_more_route_drivers_exist()
    {
        FDriver[] OnRoute(int n) => Enumerable.Range(0, n).Select(i => D("R" + i, "Haldwani", Haldwani, R(Haldwani, Nainital))).ToArray();
        var extra = D("Near", "Bhimtal", Bhimtal);

        var two = await Run([.. OnRoute(2), extra], Haldwani, Nainital);
        Assert.False(Tier(two, "basedNearFrom").Collapsed);
        Assert.False(Tier(two, "inTo").Collapsed);

        var three = await Run([.. OnRoute(3), extra], Haldwani, Nainital);
        Assert.True(Tier(three, "basedNearFrom").Collapsed);
        Assert.True(Tier(three, "inTo").Collapsed);
        Assert.False(Tier(three, "onRoute").Collapsed);

        var mix = OnRoute(1);
        var passing = D("P", "Bhimtal", Bhimtal);
        var passing2 = D("P2", "Bhimtal", Bhimtal);
        var m = await Run([.. mix, passing, passing2], Haldwani, Nainital, ways: TaxiFeedFixture.Ways(passing, passing2));
        Assert.True(Tier(m, "basedNearFrom").Collapsed);
    }

    [Fact]
    public async Task On_route_pages_by_number_and_reports_more()
    {
        var ds = Enumerable.Range(0, 7).Select(i => D("R" + i, "Haldwani", Haldwani, R(Haldwani, Nainital))).ToArray();
        var first = await Run(ds, Haldwani, Nainital, size: 3);
        var t1 = Tier(first, "onRoute");
        Assert.Equal(3, t1.Items.Count);
        Assert.True(t1.HasMore);
        Assert.Equal("p2", t1.NextToken);
        var second = await Run(ds, Haldwani, Nainital, size: 3, tier: "onRoute", token: "p2");
        var t2 = second.Tiers.Single();
        Assert.Equal(3, t2.Items.Count);
        Assert.Equal("p3", t2.NextToken);
        Assert.Empty(t1.Items.Select(i => i.Id).Intersect(t2.Items.Select(i => i.Id)));
    }

    [Fact]
    public async Task Route_drivers_beyond_the_first_page_are_not_repeated_as_based_nearby()
    {
        var ds = Enumerable.Range(0, 4).Select(i => D("R" + i, "Haldwani", Haldwani, R(Haldwani, Nainital))).ToArray();
        var d = await Run(ds, Haldwani, Nainital, size: 2);
        Assert.Empty(Tier(d, "basedNearFrom").Items);
        Assert.Empty(Tier(d, "inTo").Items);
    }

    [Fact]
    public async Task Show_more_on_a_distance_tier_returns_that_tier_only_without_repeats()
    {
        var ds = Enumerable.Range(0, 6).Select(i => D("T" + i, "Bhimtal", (29.30 + i * 0.07, 79.55))).ToArray();
        var first = await Run(ds, Haldwani, Nainital, size: 2);
        var from = Tier(first, "basedNearFrom");
        Assert.True(from.HasMore);
        var more = await Run(ds, Haldwani, Nainital, size: 2, tier: "basedNearFrom", token: from.NextToken);
        var t = more.Tiers.Single();
        Assert.Equal("basedNearFrom", t.Key);
        Assert.NotEmpty(t.Items);
        Assert.Empty(from.Items.Select(i => i.Id).Intersect(t.Items.Select(i => i.Id)));
        Assert.False(t.Collapsed);
        var toMore = await Run(ds, Haldwani, Nainital, size: 2, tier: "inTo");
        Assert.Equal("inTo", toMore.Tiers.Single().Key);
    }

    [Fact]
    public async Task No_item_in_any_tier_carries_coordinates()
    {
        var d = await Run([D("A", "Haldwani", Haldwani, R(Haldwani, Nainital)), D("B", "Bhimtal", Bhimtal)], Haldwani, Nainital);
        var json = System.Text.Json.JsonSerializer.Serialize(d).ToLowerInvariant();
        Assert.DoesNotContain("\"lat", json);
        Assert.DoesNotContain("\"lng", json);
        Assert.DoesNotContain("longitude", json);
    }
}
