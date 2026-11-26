using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Tests.Owner.Be1;
using RentNearBy.Tests.Taxis;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.TaxiAvailability;

// Wire shapes of contract 1.1-1.7 (field names the app parses), error table, cache keys and the route-feed composer rules (R12, R20, R21).
public class TaxiAvailabilityContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(OwnerErrorTypes.DayOffBooked, 409, "This day has a booking")]
    [InlineData(OwnerErrorTypes.DayOffPast, 409, "Pick today or a later day.")]
    [InlineData(OwnerErrorTypes.DayOffReasonTooLong, 400, "Keep the reason under 200 characters.")]
    [InlineData(OwnerErrorTypes.DayIsOff, 409, "This day is off. Change it on Home.")]
    [InlineData(OwnerErrorTypes.PopularRouteDuplicate, 409, "This route is already added.")]
    [InlineData(OwnerErrorTypes.PopularRouteSameCity, 400, "From and To must be different places")]
    [InlineData(OwnerErrorTypes.PopularRouteCityUnknown, 400, "Pick a city from the list.")]
    [InlineData(OwnerErrorTypes.PopularRouteLimit, 409, "You can add up to 30 routes.")]
    [InlineData(OwnerErrorTypes.PopularRouteOrderStale, 409, "The list changed. Reload.")]
    public void New_error_types_have_their_status_and_message(string type, int status, string message)
    {
        Assert.Contains(type, OwnerErrorTypes.All);
        Assert.Equal(status, OwnerErrorTypes.StatusOf(type));
        Assert.Equal(message, OwnerErrorTypes.MessageOf(type));
    }

    [Fact]
    public void Error_type_strings_match_the_app_constants()
    {
        Assert.Equal("DayOffBooked", OwnerErrorTypes.DayOffBooked);
        Assert.Equal("DayOffPast", OwnerErrorTypes.DayOffPast);
        Assert.Equal("DayOffReasonTooLong", OwnerErrorTypes.DayOffReasonTooLong);
        Assert.Equal("DayIsOff", OwnerErrorTypes.DayIsOff);
    }

    [Fact]
    public void Day_is_off_carries_the_date()
    {
        var e = OwnerCalendarWriter.DayIsOff(new DateOnly(2026, 11, 27));
        Assert.Equal(OwnerErrorTypes.DayIsOff, e.Type);
        Assert.Equal("{\"date\":\"2026-11-27\"}", JsonSerializer.Serialize(e.Data, Web));
    }

    [Fact]
    public void Days_off_state_serialises_as_contract_a1()
    {
        var ctx = Be1Kit.Context(ListingKinds.Taxi, Be1Kit.Free(ListingKinds.Taxi));
        var row = new TaxiDayOffService.StateRow
        {
            DaysOff = """[{"date":"2026-11-27","reason":"Family function in Almora"},{"date":"2026-11-30","reason":null}]""",
            BookedDays = """["2026-11-28"]""", MonthBookings = 3, MonthDaysOff = 2, SwitchOn = true,
        };
        var json = JsonSerializer.SerializeToElement(TaxiDayOffService.Map(ctx, row), Web);
        Assert.Equal("2026-11-21", json.GetProperty("today").GetString());
        Assert.Equal(0, json.GetProperty("monthsAhead").GetInt32());
        Assert.True(json.GetProperty("switchOn").GetBoolean());
        var window = json.GetProperty("window");
        foreach (var f in new[] { "upcomingTo", "monthFrom", "monthTo", "manualFrom", "manualTo", "locked" }) Assert.True(window.TryGetProperty(f, out _), f);
        Assert.Equal("2026-11-30", window.GetProperty("monthTo").GetString());
        var days = json.GetProperty("daysOff");
        Assert.Equal(2, days.GetArrayLength());
        Assert.Equal("2026-11-27", days[0].GetProperty("date").GetString());
        Assert.Equal("Family function in Almora", days[0].GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, days[1].GetProperty("reason").ValueKind);
        Assert.Equal("2026-11-28", json.GetProperty("bookedDays")[0].GetString());
        var counts = json.GetProperty("counts");
        Assert.Equal("2026-11", counts.GetProperty("month").GetString());
        Assert.Equal(3, counts.GetProperty("bookings").GetInt32());
        Assert.Equal(2, counts.GetProperty("daysOff").GetInt32());
    }

    [Fact]
    public void Broken_state_json_reads_as_empty_lists()
    {
        var ctx = Be1Kit.Context(ListingKinds.Taxi, Be1Kit.Free(ListingKinds.Taxi));
        var dto = TaxiDayOffService.Map(ctx, new TaxiDayOffService.StateRow { DaysOff = "not json", BookedDays = "" });
        Assert.Empty(dto.DaysOff);
        Assert.Empty(dto.BookedDays);
    }

    [Fact]
    public void Home_numbers_carry_the_availability_object_and_old_json_still_parses()
    {
        var dto = new OwnerHomeNumbersDto { Taxi = new OwnerTaxiNumbersDto(1, null, null, 0, 0, new TaxiAvailabilityCountsDto("2026-11", 3, 2)) };
        var json = JsonSerializer.SerializeToElement(dto, Web);
        var a = json.GetProperty("taxi").GetProperty("availability");
        Assert.Equal("2026-11", a.GetProperty("month").GetString());
        Assert.Equal(3, a.GetProperty("bookings").GetInt32());
        Assert.Equal(2, a.GetProperty("daysOff").GetInt32());
        Assert.Contains("\"localDayRate\"", JsonSerializer.Serialize(new OwnerHomeListingDto { LocalDayRate = 1200 }, Web));

        var old = JsonSerializer.Deserialize<OwnerTaxiNumbersDto>("""{"newRequests":1,"ratingCount":0,"missedRequests":3}""", Web)!;
        Assert.Null(old.Availability);
        Assert.Equal(3, old.MissedRequests);
    }

    [Fact]
    public void Popular_public_and_admin_items_use_the_contract_names()
    {
        var pub = JsonSerializer.SerializeToElement(new PopularRoutePublicDto(Guid.Empty, 101, "Delhi", 28.6, 77.2, 245, "Rishikesh", 30.08, 78.26, 899, 9), Web);
        foreach (var f in new[] { "id", "fromCityKey", "fromName", "fromLat", "fromLng", "toCityKey", "toName", "toLat", "toLng", "minFare", "taxiCount" })
            Assert.True(pub.TryGetProperty(f, out _), f);
        Assert.Equal(11, pub.EnumerateObject().Count());
        var admin = JsonSerializer.SerializeToElement(new PopularRouteAdminDto(Guid.Empty, 101, "Delhi", 245, "Rishikesh", 1, true), Web);
        foreach (var f in new[] { "id", "fromCityKey", "fromCityName", "toCityKey", "toCityName", "sortOrder", "isActive" })
            Assert.True(admin.TryGetProperty(f, out _), f);
        Assert.Equal(7, admin.EnumerateObject().Count());
    }

    [Fact]
    public void Route_feed_dto_has_date_and_free_count()
    {
        var json = JsonSerializer.SerializeToElement(new TaxiRouteFeedDto { Date = "2026-11-26", FreeCount = 9 }, Web);
        Assert.Equal("2026-11-26", json.GetProperty("date").GetString());
        Assert.Equal(9, json.GetProperty("freeCount").GetInt32());
    }

    // ---- popular routes rules ----

    [Fact]
    public void Same_city_or_missing_key_is_refused_without_a_call()
    {
        Assert.Equal(OwnerErrorTypes.PopularRouteSameCity, PopularRouteAdminStore.CheckPair(101, 101)!.Type);
        Assert.Equal(OwnerErrorTypes.PopularRouteCityUnknown, PopularRouteAdminStore.CheckPair(null, 101)!.Type);
        Assert.Equal(OwnerErrorTypes.PopularRouteCityUnknown, PopularRouteAdminStore.CheckPair(101, 0)!.Type);
        Assert.Null(PopularRouteAdminStore.CheckPair(101, 245));
    }

    [Fact]
    public void Order_must_name_exactly_the_stored_ids_once()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        Assert.True(PopularRouteAdminStore.SameSet([b, a], [a, b]));
        Assert.False(PopularRouteAdminStore.SameSet([a], [a, b]));
        Assert.False(PopularRouteAdminStore.SameSet([a, a], [a, b]));
        Assert.False(PopularRouteAdminStore.SameSet([a, c], [a, b]));
        Assert.True(PopularRouteAdminStore.SameSet([], []));
        Assert.Equal(30, PopularRouteLimits.Max);
        Assert.Contains("HAVING count(*) < 30", PopularRouteAdminStore.InsertSql);
    }

    [Fact]
    public async Task Popular_names_come_from_live_cities_and_a_dead_city_drops_the_route()
    {
        var r = CityFixture.Resolver(CityFixture.Haldwani, CityFixture.Nainital);
        var rows = new List<PopularRouteReader.Row>
        {
            new() { Id = Guid.NewGuid(), FromCityKey = CityFixture.Haldwani.CityKey, ToCityKey = CityFixture.Nainital.CityKey, Taxis = 4, MinFare = 900 },
            new() { Id = Guid.NewGuid(), FromCityKey = CityFixture.Haldwani.CityKey, ToCityKey = 999_999_999, Taxis = 2, MinFare = 500 },
        };
        var (items, available) = await PopularRouteReader.NameAsync(rows, r);
        Assert.True(available);
        var only = Assert.Single(items);
        Assert.Equal(CityFixture.Haldwani.Name, only.FromName);
        Assert.Equal(CityFixture.Nainital.Name, only.ToName);
        Assert.Equal(4, only.TaxiCount);
        Assert.Equal(900, only.MinFare);
    }

    [Fact]
    public async Task No_city_catalog_answers_an_empty_uncached_list()
    {
        var rows = new List<PopularRouteReader.Row> { new() { Id = Guid.NewGuid(), FromCityKey = 1, ToCityKey = 2, Taxis = 1, MinFare = 100 } };
        var (items, available) = await PopularRouteReader.NameAsync(rows, new DownResolver());
        Assert.False(available);
        Assert.Empty(items);
    }

    private sealed class DownResolver : ICityResolver
    {
        public double BoxKm => 10;
        public Task<CityPin> ResolveAsync(double lat, double lng) => Task.FromResult(new CityPin(false, null));
        public Task<CityPin> FindAsync(long cityKey) => Task.FromResult(new CityPin(false, null));
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey)
            => Task.FromResult<IReadOnlyList<T>>([]);
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    // ---- cache keys ----

    [Fact]
    public void Dated_keys_differ_per_day_and_undated_keys_are_unchanged()
    {
        var district = Guid.NewGuid();
        var plain = TaxiCache.LocalKey(district, null, null, 10);
        Assert.Equal(plain, plain + TaxiCache.DayPart(null));
        Assert.NotEqual(TaxiCache.DayPart(new DateOnly(2026, 11, 27)), TaxiCache.DayPart(new DateOnly(2026, 11, 28)));
        Assert.Equal("taxis:avail:gen", ListingCacheKeys.TaxiAvailGen);
        Assert.Equal("taxis:popular:gen", ListingCacheKeys.TaxiPopularGen);
        Assert.Equal("taxis:popular:20261126", ListingCacheKeys.TaxiPopular(new DateOnly(2026, 11, 26)));
        Assert.Equal(new[] { ListingCacheKeys.TaxiSearchGen, ListingCacheKeys.TaxiAvailGen, ListingCacheKeys.TaxiPopularGen }, PopularRouteReader.GenKeys.ToArray());
    }

    [Fact]
    public void Sum_stamp_moves_when_any_generation_moves()
    {
        var before = FeedCache.SumStamp(["4", "7", null]);
        Assert.Equal(11, before);
        Assert.NotEqual(before, FeedCache.SumStamp(["4", "8", null]));
        Assert.NotEqual(before, FeedCache.SumStamp(["4", "7", "1"]));
        Assert.Null(CacheStamp.Unwrap(CacheStamp.Wrap(before, "x"), FeedCache.SumStamp(["5", "7", null])));
    }

    // ---- route-feed composer: date, sort, passing filter, freeCount ----

    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130);
    private static readonly (double Lat, double Lng) Nainital = (29.3919, 79.4542);

    private static async Task<TaxiRouteFeedDto> Route(IReadOnlyList<FDriver> ds, FDriver[] passing, string? tier = null, DateOnly? day = null,
        bool cheapest = false, TaxiBusyIds? busy = null, TaxiFreeCount? free = null)
        => await TaxiFeedComposer.RouteAsync(
            new TaxiRouteFeedArgs(Haldwani.Lat, Haldwani.Lng, Nainital.Lat, Nainital.Lng, null, 5, tier, null, day, cheapest),
            TaxiFeedFixture.OnRoute(ds, Haldwani.Lat, Haldwani.Lng, Nainital.Lat, Nainital.Lng), TaxiFeedFixture.Ways(passing),
            new FakeFeedQuery(ds), new FakeFeedContext(), TaxiFeedFixture.Hydrate(ds), default, busy, free);

    [Fact]
    public async Task Passing_drivers_not_free_on_the_day_are_dropped_before_the_cap()
    {
        var p1 = new FDriver("P1", "Bhimtal", 29.34, 79.55, []);
        var p2 = new FDriver("P2", "Bhimtal", 29.34, 79.55, []);
        var dto = await Route([p1, p2], [p1, p2], day: new DateOnly(2026, 11, 27),
            busy: ids => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid> { p1.Id }));
        var passing = dto.Tiers.Single(t => t.Key == TaxiTierKeys.Passing).Items;
        Assert.Equal(new[] { p2.Id }, passing.Select(i => i.Id).ToArray());
        Assert.Equal("2026-11-27", dto.Date);
    }

    [Fact]
    public async Task Free_count_is_sent_on_the_first_call_and_null_on_tier_pages()
    {
        var p = new FDriver("P", "Bhimtal", 29.34, 79.55, []);
        IReadOnlyCollection<Guid>? seen = null;
        TaxiFreeCount free = ids => { seen = ids; return Task.FromResult(7); };
        var first = await Route([p], [p], day: new DateOnly(2026, 11, 27), free: free);
        Assert.Equal(7, first.FreeCount);
        Assert.Equal(new[] { p.Id }, seen!.ToArray());
        var page = await Route([p], [p], tier: TaxiTierKeys.InTo, day: new DateOnly(2026, 11, 27));
        Assert.Null(page.FreeCount);
        Assert.Equal("2026-11-27", page.Date);
    }

    [Fact]
    public async Task Cheapest_orders_passing_by_fare_with_unknown_fares_last()
    {
        var ways = new TaxiWaysResultDto
        {
            AllTheWay =
            [
                new WayDriverDto { TaxiId = Guid.NewGuid(), DriverName = "A", ListedFare = 5000, VehicleCategory = "Sedan", FromName = "A", ToName = "B" },
                new WayDriverDto { TaxiId = Guid.NewGuid(), DriverName = "B", ListedFare = 1200, VehicleCategory = "Sedan", FromName = "A", ToName = "B" },
                new WayDriverDto { TaxiId = Guid.NewGuid(), DriverName = "C", ListedFare = 3000, VehicleCategory = "Sedan", FromName = "A", ToName = "B" },
            ],
        };
        var items = await TaxiFeedComposer.PassingAsync(() => Task.FromResult<TaxiWaysResultDto?>(ways), null, [], 20, null, cheapest: true);
        Assert.Equal(new[] { "B", "C", "A" }, items.Select(i => i.DriverName).ToArray());
        var nearest = await TaxiFeedComposer.PassingAsync(() => Task.FromResult<TaxiWaysResultDto?>(ways), null, [], 2);
        Assert.Equal(new[] { "A", "B" }, nearest.Select(i => i.DriverName).ToArray());
    }

    [Fact]
    public async Task The_day_reaches_the_distance_tiers_through_the_filter()
    {
        var seen = new List<NearbyFilter>();
        var query = new RecordingQuery(seen);
        await TaxiFeedComposer.RouteAsync(
            new TaxiRouteFeedArgs(Haldwani.Lat, Haldwani.Lng, Nainital.Lat, Nainital.Lng, null, 5, null, null, new DateOnly(2026, 11, 27)),
            (_, _) => Task.FromResult<(IReadOnlyList<TaxiBrowseDto>, bool)>(([], false)), TaxiFeedFixture.NoWays(),
            query, new FakeFeedContext(), _ => Task.FromResult(new Dictionary<Guid, TaxiFeedItemDto>()));
        Assert.Equal(2, seen.Count);
        Assert.All(seen, f => Assert.Equal(new DateOnly(2026, 11, 27), f.Day));
    }

    [Fact]
    public async Task The_local_feed_and_featured_get_the_day()
    {
        var seen = new List<NearbyFilter>();
        await TaxiFeedComposer.LocalAsync(new NearbyArgs(Haldwani.Lat, Haldwani.Lng, 10, null), null, new RecordingQuery(seen), new FakeFeedContext(),
            _ => Task.FromResult(new Dictionary<Guid, TaxiFeedItemDto>()), default, new DateOnly(2026, 11, 28));
        Assert.Equal(2, seen.Count);
        Assert.All(seen, f => Assert.Equal(new DateOnly(2026, 11, 28), f.Day));
    }

    private sealed class RecordingQuery(List<NearbyFilter> seen) : INearbyFeedQuery
    {
        public Task<IReadOnlyList<NearbyRow>> PageAsync(NearbyKind kind, double lat, double lng, NearbyToken? after, int take, NearbyFilter filter, CancellationToken ct = default)
        {
            seen.Add(filter);
            return Task.FromResult<IReadOnlyList<NearbyRow>>([]);
        }

        public Task<IReadOnlyList<NearbyRow>> FeaturedAsync(NearbyKind kind, double lat, double lng, double maxKm, int take, NearbyFilter filter, CancellationToken ct = default)
        {
            seen.Add(filter);
            return Task.FromResult<IReadOnlyList<NearbyRow>>([]);
        }
    }

    // ---- handler answers ----

    private sealed class Allow : IRateLimitService
    {
        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1) => Task.FromResult(new RateLimitResult(true, maxAttempts, null));
        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("soon")]
    public async Task Feed_with_a_malformed_date_is_invalid_date(string date)
    {
        var r = await TaxiFeedHandlers.GetFeed(29, 79, null, null, null, null!, null!, null!, new Allow(), new ClaimsPrincipal(), new DefaultHttpContext(),
            default, date);
        var (status, _, type) = CityFixture.Read(r);
        Assert.Equal(400, status);
        Assert.Equal("InvalidDate", type);
    }

    [Fact]
    public async Task Feed_with_day_91_is_invalid_date()
    {
        var day91 = TaxiSearchDate.Text(TaxiFeedHandlers.TodayIst(null).AddDays(91));
        var r = await TaxiFeedHandlers.GetFeed(29, 79, null, null, null, null!, null!, null!, new Allow(), new ClaimsPrincipal(), new DefaultHttpContext(),
            default, day91);
        Assert.Equal("InvalidDate", CityFixture.Read(r).ErrorType);
    }

    [Fact]
    public async Task Route_feed_refuses_an_unknown_sort()
    {
        var r = await TaxiFeedHandlers.GetRouteFeed(29, 79, 30, 79, null, null, null, null, null!, null!, null!, null!, null!, new Allow(),
            Options.Create(new TaxiWaysOptions()), TaxiFeedFixture.User(), new DefaultHttpContext(), default, null, "fastest");
        Assert.Equal(400, CityFixture.Read(r).Status);
    }

    [Fact]
    public async Task Route_feed_refuses_a_date_beyond_the_window()
    {
        var r = await TaxiFeedHandlers.GetRouteFeed(29, 79, 30, 79, null, null, null, null, null!, null!, null!, null!, null!, new Allow(),
            Options.Create(new TaxiWaysOptions()), TaxiFeedFixture.User(), new DefaultHttpContext(), default,
            TaxiSearchDate.Text(TaxiFeedHandlers.TodayIst(null).AddDays(120)), "cheapest");
        Assert.Equal("InvalidDate", CityFixture.Read(r).ErrorType);
    }

    [Theory]
    [InlineData(101L, null)]
    [InlineData(101L, 101L)]
    [InlineData(0L, 245L)]
    public async Task Route_feed_refuses_half_or_bad_city_keys(long? from, long? to)
    {
        var r = await TaxiFeedHandlers.GetRouteFeed(29, 79, 30, 79, null, null, null, null, null!, null!, null!, null!, null!, new Allow(),
            Options.Create(new TaxiWaysOptions()), TaxiFeedFixture.User(), new DefaultHttpContext(), default, null, null, from, to);
        var read = CityFixture.Read(r);
        Assert.Equal(400, read.Status);
    }
}
