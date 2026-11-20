using FluentValidation.TestHelper;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// The city list picker of the driver routes: keys travel with the pick, the server checks them against the live catalog
// and stores them as sent; old builds (no keys) keep the pin lookup.
public class TaxiRouteCityPickTests
{
    private static readonly TripCityInfo Haldwani = TaxiRouteCityKeyTests.Haldwani;
    private static readonly TripCityInfo Bhimtal = TaxiRouteCityKeyTests.Bhimtal;
    private static readonly (decimal Lat, decimal Lng) Delhi = (28.6139m, 77.2090m);

    private static TaxiRouteInput Input(long? fromKey = null, long? toKey = null, bool fromOther = false, bool toOther = false,
        (decimal Lat, decimal Lng)? from = null, (decimal Lat, decimal Lng)? to = null) => new()
    {
        FromName = "Haldwani", ToName = "Bhimtal", TripType = TaxiTripTypes.All[0], Fare = 1500,
        FromLatitude = from?.Lat ?? (decimal)Haldwani.Latitude, FromLongitude = from?.Lng ?? (decimal)Haldwani.Longitude,
        ToLatitude = to?.Lat ?? (decimal)Bhimtal.Latitude, ToLongitude = to?.Lng ?? (decimal)Bhimtal.Longitude,
        FromCityKey = fromKey, ToCityKey = toKey, FromOtherPlace = fromOther, ToOtherPlace = toOther,
    };

    private sealed class Catalog(params TripCityInfo[] live) : ITripCityCatalog
    {
        public bool Down { get; init; }
        public bool Throws { get; init; }
        public List<long> Asked { get; } = [];
        public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult<TripCityCatalogSnapshot?>(null);
        public Task InvalidateAsync() => Task.CompletedTask;

        public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default)
        {
            Asked.Add(cityKey);
            if (Throws) throw new HttpRequestException("atlas down");
            return Task.FromResult<(bool, TripCityInfo?)>(Down ? (false, null) : (true, live.FirstOrDefault(c => c.CityKey == cityKey)));
        }
    }

    private sealed class NeverAnswers : ICityResolver
    {
        public int Calls;
        public double BoxKm => 10;
        public Task<CityPin> ResolveAsync(double lat, double lng) { Calls++; return new TaskCompletionSource<CityPin>().Task; }
        public Task<TrustedCityPin> ResolveTrustedAsync(double lat, double lng) { Calls++; return new TaskCompletionSource<TrustedCityPin>().Task; }
        public Task<CityPin> FindAsync(long cityKey) => new TaskCompletionSource<CityPin>().Task;
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey)
            => Task.FromResult<IReadOnlyList<T>>([]);
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    private static TaxiRoute RouteOf(TaxiRouteInput i) => new()
    {
        Id = Guid.NewGuid(), TaxiListingId = Guid.NewGuid(), RouteIndex = 1, FromName = i.FromName, ToName = i.ToName, TripType = i.TripType, Fare = i.Fare,
        FromLatitude = i.FromLatitude, FromLongitude = i.FromLongitude, ToLatitude = i.ToLatitude, ToLongitude = i.ToLongitude,
    };

    // ---- shape (pure, also the validator rule)

    [Fact]
    public void Old_build_names_no_end_and_has_no_pick()
    {
        var input = Input();
        Assert.Null(TaxiRouteCityPicks.ShapeError(input));
        Assert.Null(TaxiRouteCityPicks.Of(input));
    }

    [Fact]
    public void Two_city_keys_are_a_pick()
    {
        var input = Input(Haldwani.CityKey, Bhimtal.CityKey);
        Assert.Null(TaxiRouteCityPicks.ShapeError(input));
        Assert.Equal(new RouteCityPick(Haldwani.CityKey, Bhimtal.CityKey), TaxiRouteCityPicks.Of(input));
    }

    [Fact]
    public void A_city_and_an_other_place_are_a_pick_with_a_null_key()
    {
        var input = Input(Haldwani.CityKey, toOther: true);
        Assert.Null(TaxiRouteCityPicks.ShapeError(input));
        Assert.Equal(new RouteCityPick(Haldwani.CityKey, null), TaxiRouteCityPicks.Of(input));
        Assert.Equal(new RouteCityPick(null, null), TaxiRouteCityPicks.Of(Input(fromOther: true, toOther: true)));
    }

    [Theory]
    [InlineData(210000101L, null, true, false, TaxiRouteCityPicks.BothMessage)]
    [InlineData(0L, 210000103L, false, false, TaxiRouteCityPicks.KeyMessage)]
    [InlineData(210000101L, -5L, false, false, TaxiRouteCityPicks.KeyMessage)]
    [InlineData(210000101L, null, false, false, TaxiRouteCityPicks.OneEndMessage)]
    [InlineData(null, null, true, false, TaxiRouteCityPicks.OneEndMessage)]
    public void Bad_shapes_are_refused(long? fromKey, long? toKey, bool fromOther, bool toOther, string message)
        => Assert.Equal(message, TaxiRouteCityPicks.ShapeError(Input(fromKey, toKey, fromOther, toOther)));

    [Fact]
    public void Validator_refuses_a_bad_shape_with_the_city_error_type_and_accepts_old_and_new_payloads()
    {
        var validator = new UpsertTaxiRoutesRequestValidator();
        var bad = validator.TestValidate(new UpsertTaxiRoutesRequest { Routes = [Input(Haldwani.CityKey)] });
        var error = Assert.Single(bad.Errors);
        Assert.Equal((TaxiErrorTypes.TaxiRouteCityInvalid, TaxiRouteCityPicks.OneEndMessage), (error.ErrorCode, error.ErrorMessage));

        validator.TestValidate(new UpsertTaxiRoutesRequest { Routes = [Input()] }).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new UpsertTaxiRoutesRequest { Routes = [Input(Haldwani.CityKey, Bhimtal.CityKey)] }).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new UpsertTaxiRoutesRequest { Routes = [Input(Haldwani.CityKey, toOther: true)] }).ShouldNotHaveAnyValidationErrors();
    }

    // ---- one end against the catalog (pure)

    [Fact]
    public void End_check_answers_by_catalog_state()
    {
        var lat = (decimal)Haldwani.Latitude;
        var lng = (decimal)Haldwani.Longitude;
        Assert.Null(TaxiRouteCityPicks.CheckEnd(lat, lng, true, Haldwani));
        Assert.Equal(new TaxiRouteCityError(503, TripErrorTypes.CitiesUnavailable, TaxiRouteCityPicks.UnavailableMessage), TaxiRouteCityPicks.CheckEnd(lat, lng, false, null));
        Assert.Equal(new TaxiRouteCityError(400, "TaxiRouteCityInvalid", TaxiRouteCityPicks.NotLiveMessage), TaxiRouteCityPicks.CheckEnd(lat, lng, true, null));
        Assert.Equal(new TaxiRouteCityError(400, "TaxiRouteCityInvalid", TaxiRouteCityPicks.FarPinMessage), TaxiRouteCityPicks.CheckEnd(Delhi.Lat, Delhi.Lng, true, Haldwani));
    }

    [Fact]
    public void A_pin_inside_the_city_but_off_its_centre_is_still_that_city()
        => Assert.Null(TaxiRouteCityPicks.CheckEnd(29.3000m, 79.5300m, true, Haldwani));

    // ---- CheckPicksAsync

    private static (TaxiRouteInput, RouteCityPick?, RouteEndsBefore?) Row(TaxiRouteInput input, RouteEndsBefore? before = null)
        => (input, TaxiRouteCityPicks.Of(input), before);

    [Fact]
    public async Task Live_keys_pass_and_each_key_is_read_once()
    {
        var catalog = new Catalog(Haldwani, Bhimtal);
        var error = await TaxiRouteCityKeys.CheckPicksAsync(catalog,
            [Row(Input(Haldwani.CityKey, Bhimtal.CityKey)), Row(Input(Bhimtal.CityKey, Haldwani.CityKey, from: ((decimal)Bhimtal.Latitude, (decimal)Bhimtal.Longitude), to: ((decimal)Haldwani.Latitude, (decimal)Haldwani.Longitude)))],
            TestContext.Current.CancellationToken);
        Assert.Null(error);
        Assert.Equal([Haldwani.CityKey, Bhimtal.CityKey], catalog.Asked);
    }

    [Fact]
    public async Task A_key_that_is_not_a_live_city_is_refused_with_400()
    {
        var error = await TaxiRouteCityKeys.CheckPicksAsync(new Catalog(Haldwani), [Row(Input(Haldwani.CityKey, Bhimtal.CityKey))], TestContext.Current.CancellationToken);
        Assert.Equal((400, TaxiErrorTypes.TaxiRouteCityInvalid, TaxiRouteCityPicks.NotLiveMessage), (error!.Status, error.Type, error.Message));
    }

    [Fact]
    public async Task Catalog_down_or_failing_answers_503_never_a_guess()
    {
        var down = await TaxiRouteCityKeys.CheckPicksAsync(new Catalog { Down = true }, [Row(Input(Haldwani.CityKey, Bhimtal.CityKey))], TestContext.Current.CancellationToken);
        var throws = await TaxiRouteCityKeys.CheckPicksAsync(new Catalog { Throws = true }, [Row(Input(Haldwani.CityKey, Bhimtal.CityKey))], TestContext.Current.CancellationToken);
        Assert.Equal((503, TripErrorTypes.CitiesUnavailable), (down!.Status, down.Type));
        Assert.Equal((503, TripErrorTypes.CitiesUnavailable), (throws!.Status, throws.Type));
    }

    [Fact]
    public async Task Old_builds_and_other_places_never_touch_the_catalog()
    {
        var catalog = new Catalog { Down = true };
        Assert.Null(await TaxiRouteCityKeys.CheckPicksAsync(catalog, [Row(Input()), Row(Input(fromOther: true, toOther: true))], TestContext.Current.CancellationToken));
        Assert.Empty(catalog.Asked);
    }

    [Fact]
    public async Task A_key_the_slot_already_holds_is_kept_even_after_the_city_left_the_list()
    {
        var input = Input(Haldwani.CityKey, Bhimtal.CityKey);
        var before = new RouteEndsBefore(input.FromLatitude, input.FromLongitude, Haldwani.CityKey, input.ToLatitude, input.ToLongitude, Bhimtal.CityKey);
        var catalog = new Catalog { Down = true };
        Assert.Null(await TaxiRouteCityKeys.CheckPicksAsync(catalog, [Row(input, before)], TestContext.Current.CancellationToken));
        Assert.Empty(catalog.Asked);
    }

    [Fact]
    public async Task A_far_pin_for_a_new_key_is_refused()
    {
        var error = await TaxiRouteCityKeys.CheckPicksAsync(new Catalog(Haldwani, Bhimtal), [Row(Input(Haldwani.CityKey, Bhimtal.CityKey, from: Delhi))], TestContext.Current.CancellationToken);
        Assert.Equal((400, TaxiRouteCityPicks.FarPinMessage), (error!.Status, error.Message));
    }

    // ---- ResolveForSaveAsync with picks

    [Fact]
    public async Task Picked_keys_are_stored_as_sent_final_and_without_any_lookup()
    {
        var resolver = new NeverAnswers();
        var picked = RouteOf(Input());
        var other = RouteOf(Input());
        var log = new RecordingLogger();

        var (unresolved, keys) = await TaxiRouteCityKeys.ResolveForSaveAsync(resolver, [(picked, null), (other, null)],
                [new RouteCityPick(Haldwani.CityKey, Bhimtal.CityKey), new RouteCityPick(Haldwani.CityKey, null)], log, TimeSpan.FromMilliseconds(50))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, unresolved);
        Assert.Empty(log.Entries);
        Assert.Equal((Haldwani.CityKey, (long?)Bhimtal.CityKey, true), (picked.FromCityKey, picked.ToCityKey, picked.CityKeysChosen));
        Assert.Equal((Haldwani.CityKey, (long?)null, true), (other.FromCityKey, other.ToCityKey, other.CityKeysChosen));
        Assert.Equal([new RouteServeKeys(Haldwani.CityKey, Bhimtal.CityKey), new RouteServeKeys(Haldwani.CityKey, null)], keys);
    }

    [Fact]
    public async Task A_picked_key_wins_over_the_key_the_slot_had()
    {
        var route = RouteOf(Input());
        route.FromCityKey = Bhimtal.CityKey;
        var before = RouteEndsBefore.Of(route);
        await TaxiRouteCityKeys.ResolveForSaveAsync(new NeverAnswers(), [(route, before)], [new RouteCityPick(Haldwani.CityKey, null)], new RecordingLogger(), TimeSpan.Zero);
        Assert.Equal((Haldwani.CityKey, (long?)null), (route.FromCityKey, route.ToCityKey));
    }

    [Fact]
    public async Task An_old_build_save_clears_the_chosen_mark_and_looks_up_the_pins()
    {
        var resolver = new NeverAnswers();
        var route = RouteOf(Input());
        route.CityKeysChosen = true;
        var (unresolved, keys) = await TaxiRouteCityKeys.ResolveForSaveAsync(resolver, [(route, null)], new RouteCityPick?[1], new RecordingLogger(), TimeSpan.FromMilliseconds(50))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(route.CityKeysChosen);
        Assert.True(resolver.Calls > 0);
        Assert.Equal(1, unresolved);
        Assert.True(keys[0].FromPending && keys[0].ToPending);
    }

    [Fact]
    public async Task Pick_list_must_match_the_routes()
        => await Assert.ThrowsAsync<ArgumentException>(() =>
            TaxiRouteCityKeys.ResolveForSaveAsync(new NeverAnswers(), [(RouteOf(Input()), null)], [], new RecordingLogger(), TimeSpan.Zero));

    // ---- backfill never fills a chosen route

    [Fact]
    public void Backfill_reads_only_routes_whose_keys_were_not_chosen()
    {
        Assert.Contains("AND NOT \"CityKeysChosen\"", TaxiRouteCityKeyStore.NextMissingSql);
        Assert.Contains("(\"FromCityKey\" IS NULL OR \"ToCityKey\" IS NULL)", TaxiRouteCityKeyStore.NextMissingSql);
    }

    [Fact]
    public void Backfill_write_skips_a_route_that_became_chosen()
    {
        var source = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRouteCityKeys.cs");
        Assert.Contains(".Where(x => x.Id == r.Id && !x.CityKeysChosen", source);
    }

    // ---- route DTO for the app

    [Theory]
    [InlineData(210000101L, 210000103L, true, true, false, false)]
    [InlineData(210000101L, null, true, false, false, true)]
    [InlineData(210000101L, null, false, false, false, false)]
    [InlineData(null, null, true, false, true, true)]
    public void Route_dto_says_whether_it_is_active_in_requests(long? fromKey, long? toKey, bool chosen, bool active, bool fromOther, bool toOther)
    {
        var dto = new TaxiRouteDto { FromCityKey = fromKey, ToCityKey = toKey, CityKeysChosen = chosen };
        Assert.Equal((active, fromOther, toOther), (dto.ActiveInRequests, dto.FromOtherPlace, dto.ToOtherPlace));
        var json = System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"activeInRequests\":" + active.ToString().ToLowerInvariant(), json);
        Assert.Contains("\"cityKeysChosen\":", json);
        Assert.Contains("\"fromOtherPlace\":", json);
        Assert.Contains("\"toOtherPlace\":", json);
    }
}

// The no ready driver check at create and the end reason of a queue that ran out.
public class TaxiNoReadyDriverTests
{
    [Theory]
    [InlineData(0, 0, 0, "NoReadyDriver")]
    [InlineData(3, 0, 0, "NoReadyDriver")]
    [InlineData(3, 2, 0, "AllDriversBusy")]
    [InlineData(3, 2, 1, null)]
    public void Create_needs_one_ringable_driver(int listed, int ringReady, int ringable, string? type)
    {
        var error = TaxiRequestCreateRules.ReadinessError(new TaxiCreateReadiness(listed, ringReady, ringable));
        Assert.Equal(type, error?.Type);
        if (error is null) return;
        Assert.Equal(409, error.Status);
        Assert.Equal(TaxiRequestErrorTypes.Messages[type!], error.Message);
    }

    [Fact]
    public void Readiness_sql_uses_the_list_rule_for_listed_and_adds_the_ring_rule_for_ringable()
    {
        var sql = TaxiCandidateQuery.ReadinessSql;
        Assert.Contains($"WHERE {TaxiCandidateQuery.ListRule}", sql);
        Assert.Contains($"FILTER (WHERE {TaxiCandidateQuery.RingRule})::int AS \"RingReady\"", sql);
        Assert.Contains($"FILTER (WHERE {TaxiCandidateQuery.RingRule} AND {TaxiCandidateQuery.NoBookingOverlapRule} AND {TaxiCandidateQuery.NoLegacyOverlapRule})", sql);
        Assert.Contains($"JOIN \"TaxiListings\" l ON {TaxiCandidateQuery.RouteRule}", sql);
        Assert.Contains(TaxiCandidateQuery.SeatsRule, sql);
        Assert.Contains(TaxiCandidateQuery.NotTravellerRule, sql);
        Assert.Contains(TaxiCandidateQuery.NoBlockRule, sql);
        // Busy drivers still count: the free rule is not part of the check.
        Assert.DoesNotContain(TaxiCandidateQuery.FreeRule, sql);
        foreach (var column in new[] { "MatchToCityKey", "MatchFromCityKey", "Seats", "TravellerUserId", "DateFrom", "DateTo" })
            Assert.Contains($"AS \"{column}\"", sql);
    }

    [Fact]
    public void Create_runs_the_readiness_check_under_the_trip_lock_after_the_overlap_check()
    {
        var source = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRequestService.cs");
        var overlap = source.IndexOf("TaxiRequestCreateRules.OverlapError(overlap)", StringComparison.Ordinal);
        var ready = source.IndexOf("await TaxiCandidateQuery.CountReadyAsync(db, userId, createPlan, ct);", StringComparison.Ordinal);
        var insert = source.IndexOf("db.Set<TaxiRequest>().Add(request);", StringComparison.Ordinal);
        Assert.True(overlap > 0 && ready > overlap && insert > ready);
    }

    [Theory]
    [InlineData(0, "NoReadyDriver")]
    [InlineData(1, "AllDriversBusy")]
    [InlineData(4, "AllDriversBusy")]
    public void A_queue_that_runs_out_tells_never_rung_from_busy(int offersEver, string reason)
        => Assert.Equal(reason, RequestEndReasons.WhenQueueRunsOut(offersEver));

    [Fact]
    public void Live_counts_also_count_every_offer_ever()
        => Assert.Contains("count(*)::int AS \"Total\"", TaxiRequestStoreSql.LiveCounts);

    [Fact]
    public void Matcher_ends_a_run_out_queue_with_the_derived_reason()
    {
        var source = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "Matching", "PostgresMatcherPassUnit.cs");
        Assert.Contains("RequestEndReasons.WhenQueueRunsOut(live.Total)", source);
        Assert.DoesNotContain("RequestEndReasons.AllDriversBusy", source);
    }
}
