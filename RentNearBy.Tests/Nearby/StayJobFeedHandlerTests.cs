using Microsoft.AspNetCore.Http;
using System.Text.Json;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Nearby;

internal sealed class FakeNearbyQuery(List<NearbyRow> rows) : INearbyFeedQuery
{
    public List<(NearbyKind Kind, double Lat, double Lng, int Take, NearbyToken? After, NearbyFilter Filter)> Calls = [];

    public Task<IReadOnlyList<NearbyRow>> PageAsync(NearbyKind kind, double lat, double lng, NearbyToken? after, int take, NearbyFilter filter, CancellationToken ct = default)
    {
        Calls.Add((kind, lat, lng, take, after, filter));
        IEnumerable<NearbyRow> q = rows.OrderBy(r => r.DistanceMeters).ThenBy(r => r.Id);
        if (after != null) q = q.Where(r => r.DistanceMeters > after.DistanceMeters || (r.DistanceMeters == after.DistanceMeters && r.Id.CompareTo(after.Id) > 0));
        return Task.FromResult<IReadOnlyList<NearbyRow>>(q.Take(take).ToList());
    }

    public Task<IReadOnlyList<NearbyRow>> FeaturedAsync(NearbyKind kind, double lat, double lng, double maxKm, int take, NearbyFilter filter, CancellationToken ct = default)
        => throw new InvalidOperationException("Jobs and Home never ask for a featured slot");
}

internal sealed class FakeNearbyContext : INearbyContext
{
    public double? SeenNearestKm;
    public int HeaderCalls;

    public Task<NearbyHeaderDto> HeaderAsync(double lat, double lng, double? nearestKm, CancellationToken ct = default)
    {
        HeaderCalls++;
        SeenNearestKm = nearestKm;
        return Task.FromResult(new NearbyHeaderDto { CentreCityName = "Haldwani", CentreStateName = "Uttarakhand", NearestKm = nearestKm, FarAway = nearestKm > 15 });
    }

    public Task<NearbyCity?> NearestCityAsync(double lat, double lng, CancellationToken ct = default) => Task.FromResult<NearbyCity?>(null);
}

internal sealed class FakeCatalog(bool available, params TripCityInfo[] cities) : ITripCityCatalog
{
    public int FindCalls;
    public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult<TripCityCatalogSnapshot?>(null);
    public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default)
    {
        FindCalls++;
        return Task.FromResult((available, available ? cities.FirstOrDefault(c => c.CityKey == cityKey) : null));
    }
    public Task InvalidateAsync() => Task.CompletedTask;
}

public class StayJobFeedHandlerTests
{
    private static readonly TripCityInfo Haldwani = CityFixture.Haldwani;

    private static (FakeNearbyQuery Query, FakeNearbyContext Ctx, StayJobFeedHandlers.Hydrate Hydrate, List<NearbyRow> Rows) Setup(params double[] meters)
    {
        var rows = meters.Select(m => new NearbyRow(Guid.NewGuid(), m)).ToList();
        var ctx = new FakeNearbyContext();
        StayJobFeedHandlers.Hydrate hydrate = (page, centre) =>
        {
            var map = new Dictionary<Guid, StayJobFeedItemDto>();
            foreach (var r in page)
            {
                var item = new StayJobFeedItemDto { Id = r.Id, Title = "Cook" };
                StayJobFeedHydrator.Fill(item, r.DistanceMeters, null, centre);
                map[r.Id] = item;
            }
            return Task.FromResult(map);
        };
        return (new FakeNearbyQuery(rows), ctx, hydrate, rows);
    }

    private static Task<IResult> Run(FakeNearbyQuery q, FakeNearbyContext c, ITripCityCatalog cat, StayJobFeedHandlers.Hydrate h,
        double? lat = null, double? lng = null, long? cityKey = null, int? pageSize = null, string? token = null,
        int? minExp = null, int? maxExp = null)
        => StayJobFeedHandlers.BuildAsync(lat, lng, cityKey, pageSize, token, null, minExp, maxExp, null, q, c, cat, h);

    [Fact]
    public async Task City_key_uses_the_city_centre_from_the_cached_catalog()
    {
        var (q, c, h, _) = Setup(1000);
        var cat = new FakeCatalog(true, Haldwani);

        var r = CityFixture.Read(await Run(q, c, cat, h, cityKey: Haldwani.CityKey));

        Assert.Equal(200, r.Status);
        Assert.Equal(NearbyKind.StayJob, q.Calls[0].Kind);
        Assert.Equal(Haldwani.Latitude, q.Calls[0].Lat);
        Assert.Equal(Haldwani.Longitude, q.Calls[0].Lng);
    }

    [Fact]
    public async Task Unknown_city_key_is_a_400()
    {
        var (q, c, h, _) = Setup(1000);

        var r = CityFixture.Read(await Run(q, c, new FakeCatalog(true, Haldwani), h, cityKey: 999));

        Assert.Equal(400, r.Status);
        Assert.Empty(q.Calls);
    }

    [Fact]
    public async Task Gps_path_works_without_a_city_key_and_does_not_touch_the_catalog()
    {
        var (q, c, h, _) = Setup(1000);
        var cat = new FakeCatalog(true, Haldwani);

        var r = CityFixture.Read(await Run(q, c, cat, h, lat: 29.2, lng: 79.5));

        Assert.Equal(200, r.Status);
        Assert.Equal(0, cat.FindCalls);
        Assert.Equal(29.2, q.Calls[0].Lat);
    }

    [Fact]
    public async Task Catalog_down_with_only_a_city_key_is_503_but_lat_lng_still_works()
    {
        var (q, c, h, _) = Setup(1000);
        var down = new FakeCatalog(false);

        var only = CityFixture.Read(await Run(q, c, down, h, cityKey: Haldwani.CityKey));
        var withGps = CityFixture.Read(await Run(q, c, down, h, lat: 29.2, lng: 79.5, cityKey: Haldwani.CityKey));

        Assert.Equal(503, only.Status);
        Assert.Equal(TripErrorTypes.CitiesUnavailable, only.ErrorType);
        Assert.Equal(200, withGps.Status);
    }

    [Fact]
    public async Task Bad_location_and_bad_token_are_400()
    {
        var (q, c, h, _) = Setup(1000);
        var cat = new FakeCatalog(true);

        Assert.Equal(400, CityFixture.Read(await Run(q, c, cat, h)).Status);
        Assert.Equal(400, CityFixture.Read(await Run(q, c, cat, h, lat: 91, lng: 0)).Status);
        Assert.Equal(400, CityFixture.Read(await Run(q, c, cat, h, lat: 29, lng: 79, token: "not-a-token")).Status);
    }

    [Fact]
    public async Task No_distance_cutoff_a_job_420_km_away_is_listed_and_labelled_far()
    {
        var (q, c, h, _) = Setup(420_000);

        var r = CityFixture.Read(await Run(q, c, new FakeCatalog(true), h, lat: 28.6, lng: 77.2));

        var item = r.Data.GetProperty("Items")[0];
        Assert.Equal("far", item.GetProperty("LabelKey").GetString());
        Assert.False(item.GetProperty("IsNear").GetBoolean());
        Assert.Equal(420.0, item.GetProperty("DistanceKm").GetDouble());
        Assert.True(r.Data.GetProperty("Header").GetProperty("FarAway").GetBoolean());
        Assert.Equal(0, r.Data.GetProperty("Featured").GetArrayLength());
    }

    [Fact]
    public async Task Order_is_by_distance_and_paging_has_no_gap_or_duplicate()
    {
        var (q, c, h, rows) = Setup(5000, 1000, 9000, 3000, 7000);
        var cat = new FakeCatalog(true);
        var seen = new List<Guid>();
        string? token = null;
        do
        {
            var r = CityFixture.Read(await Run(q, c, cat, h, lat: 29.2, lng: 79.5, pageSize: 2, token: token));
            seen.AddRange(r.Data.GetProperty("Items").EnumerateArray().Select(i => i.GetProperty("Id").GetGuid()));
            token = r.Data.GetProperty("NextToken").GetString();
        } while (token != null);

        Assert.Equal(rows.OrderBy(x => x.DistanceMeters).Select(x => x.Id), seen);
        Assert.All(q.Calls, call => Assert.Equal(3, call.Take));
    }

    [Fact]
    public async Task Nearest_km_goes_to_the_header_and_an_empty_system_gives_an_empty_page()
    {
        var (q, c, h, _) = Setup(2050);
        var one = CityFixture.Read(await Run(q, c, new FakeCatalog(true), h, lat: 29.2, lng: 79.5));
        Assert.Equal(2.1, c.SeenNearestKm);
        Assert.Equal(1, one.Data.GetProperty("Items").GetArrayLength());

        var (q2, c2, h2, _) = Setup();
        var none = CityFixture.Read(await Run(q2, c2, new FakeCatalog(true), h2, lat: 29.2, lng: 79.5));
        Assert.Equal(200, none.Status);
        Assert.Equal(0, none.Data.GetProperty("Items").GetArrayLength());
        Assert.False(none.Data.GetProperty("HasMore").GetBoolean());
        Assert.Null(c2.SeenNearestKm);
    }

    [Fact]
    public async Task Experience_filter_is_clamped_and_passed_to_the_query()
    {
        var (q, c, h, _) = Setup(1000);

        await Run(q, c, new FakeCatalog(true), h, lat: 29.2, lng: 79.5, minExp: -3, maxExp: 99);

        Assert.Equal(StayJobRules.MinExperienceYears, q.Calls[0].Filter.MinExperience);
        Assert.Equal(StayJobRules.MaxExperienceYears, q.Calls[0].Filter.MaxExperience);
    }

    [Fact]
    public async Task A_row_the_hydrator_cannot_load_is_skipped_not_an_error()
    {
        var (q, c, _, rows) = Setup(1000, 2000);
        StayJobFeedHandlers.Hydrate partial = (page, centre) =>
        {
            var item = new StayJobFeedItemDto { Id = rows[1].Id };
            StayJobFeedHydrator.Fill(item, rows[1].DistanceMeters, null, centre);
            return Task.FromResult(new Dictionary<Guid, StayJobFeedItemDto> { [item.Id] = item });
        };

        var r = CityFixture.Read(await Run(q, c, new FakeCatalog(true), partial, lat: 29.2, lng: 79.5));

        Assert.Equal(1, r.Data.GetProperty("Items").GetArrayLength());
    }

    // ---- Hydrator rules (pure) ----

    [Fact]
    public void A_village_stay_has_no_city_name_and_no_key()
    {
        var item = new StayJobFeedItemDto { DistrictName = "Nainital", StateName = "Uttarakhand", CityName = "Some Village" };

        StayJobFeedHydrator.Fill(item, 4000, null, "Haldwani");

        Assert.Null(item.CityName);
        Assert.Null(item.CityKey);
        Assert.Equal("Nainital", item.DistrictName);
        Assert.Equal("near", item.LabelKey);
    }

    [Fact]
    public void A_stay_in_the_centre_city_is_in_city_up_to_15_km_and_far_beyond()
    {
        var city = new TripCityInfo { CityKey = 210000001, Name = "Haldwani", StateName = "Uttarakhand" };
        var a = new StayJobFeedItemDto();
        var b = new StayJobFeedItemDto();
        var c = new StayJobFeedItemDto();

        StayJobFeedHydrator.Fill(a, 14_900, city, "haldwani");
        StayJobFeedHydrator.Fill(b, 15_000, city, "Haldwani");
        StayJobFeedHydrator.Fill(c, 15_100, city, "Haldwani");

        Assert.Equal("inCity", a.LabelKey);
        Assert.Equal("inCity", b.LabelKey);
        Assert.Equal("far", c.LabelKey);
        Assert.Equal(210000001, a.CityKey);
        Assert.Equal("Uttarakhand", a.StateName);
        Assert.True(a.IsNear);
        Assert.False(c.IsNear);
        Assert.Equal(15.1, c.DistanceKm);
    }

    [Fact]
    public void Feed_item_json_parses_as_the_old_job_summary()
    {
        var item = new StayJobFeedItemDto { Id = Guid.NewGuid(), Title = "Cook", DistrictName = "Nainital", CityName = "Haldwani", StateName = "Uttarakhand", CityKey = 5 };

        var back = JsonSerializer.Deserialize<StayJobSummaryDto>(JsonSerializer.Serialize(item))!;

        Assert.Equal(item.Id, back.Id);
        Assert.Equal("Haldwani", back.CityName);
    }

    [Fact]
    public void Jobs_feed_item_has_no_coordinates_or_contact_fields()
    {
        var names = typeof(StayJobFeedItemDto).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();

        Assert.DoesNotContain(names, n => n.Contains("lat") || n.Contains("lng") || n.Contains("longitude") || n.Contains("phone") || n.Contains("whatsapp"));
    }
}
