using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiRouteCityBackfillTests
{
    private static readonly TripCityInfo Haldwani = TaxiRouteCityKeyTests.Haldwani;
    private static readonly TripCityInfo Bhimtal = TaxiRouteCityKeyTests.Bhimtal;
    private static readonly (double Lat, double Lng) Delhi = (28.6139, 77.2090);

    // Same semantics as the SQL store: keyset by Id over rows with a null key; a write fills only keys that are still null.
    private sealed class MemoryStore : ITaxiRouteCityKeyStore
    {
        public readonly SortedDictionary<Guid, TaxiRouteKeyRow> Rows = new(Comparer<Guid>.Create((a, b) => string.CompareOrdinal(a.ToString(), b.ToString())));
        public int Reads, WriteCalls;
        public Func<int, bool> FailWrite = _ => false;

        public void Add((double Lat, double Lng) from, (double Lat, double Lng) to)
        {
            var id = Guid.NewGuid();
            Rows[id] = new TaxiRouteKeyRow
            {
                Id = id, FromLatitude = (decimal)from.Lat, FromLongitude = (decimal)from.Lng,
                ToLatitude = (decimal)to.Lat, ToLongitude = (decimal)to.Lng,
            };
        }

        public Task<IReadOnlyList<TaxiRouteKeyRow>> NextMissingAsync(Guid after, int take, CancellationToken ct)
        {
            Reads++;
            IReadOnlyList<TaxiRouteKeyRow> page = Rows.Values
                .Where(r => (r.FromCityKey == null || r.ToCityKey == null) && string.CompareOrdinal(r.Id.ToString(), after.ToString()) > 0)
                .Take(take)
                .Select(Clone)
                .ToList();
            return Task.FromResult(page);
        }

        public Task<int> WriteAsync(IReadOnlyList<TaxiRouteKeyWrite> writes, CancellationToken ct)
        {
            if (writes.Count == 0) return Task.FromResult(0);
            WriteCalls++;
            if (FailWrite(WriteCalls)) throw new InvalidOperationException("batch failed");
            var changed = 0;
            foreach (var w in writes)
            {
                var row = Rows[w.Row.Id];
                row.FromCityKey ??= w.FromCityKey;
                row.ToCityKey ??= w.ToCityKey;
                changed++;
            }
            return Task.FromResult(changed);
        }

        private static TaxiRouteKeyRow Clone(TaxiRouteKeyRow r) => new()
        {
            Id = r.Id, FromLatitude = r.FromLatitude, FromLongitude = r.FromLongitude, ToLatitude = r.ToLatitude,
            ToLongitude = r.ToLongitude, FromCityKey = r.FromCityKey, ToCityKey = r.ToCityKey,
        };
    }

    private static readonly Func<TimeSpan, CancellationToken, Task> NoPause = (_, _) => Task.CompletedTask;

    private static ICityResolver Resolver(bool geocoderDown = false)
        => TaxiRouteCityKeyTests.Resolver(new TaxiRouteCityKeyTests.FakeGeocoder { Down = geocoderDown });

    [Fact]
    public async Task Backfill_is_idempotent()
    {
        var store = new MemoryStore();
        for (var i = 0; i < 250; i++) store.Add((Haldwani.Latitude, Haldwani.Longitude), (Bhimtal.Latitude, Bhimtal.Longitude));
        var pauses = 0;
        var pass = new TaxiRouteCityBackfillPass(store, Resolver(), new RecordingLogger(),
            (gap, _) => { if (gap == TaxiRouteCityBackfillPass.Pause) pauses++; return Task.CompletedTask; });

        var first = await pass.RunAsync(CancellationToken.None);

        Assert.Equal(3, first.Batches);
        Assert.Equal(250, first.Written);
        Assert.Equal(0, first.Unresolved);
        Assert.Equal(2, pauses);
        Assert.All(store.Rows.Values, r =>
        {
            Assert.Equal(Haldwani.CityKey, r.FromCityKey);
            Assert.Equal(Bhimtal.CityKey, r.ToCityKey);
        });

        var snapshot = store.Rows.Values.Select(r => (r.Id, r.FromCityKey, r.ToCityKey)).ToList();
        var second = await new TaxiRouteCityBackfillPass(store, Resolver(), new RecordingLogger(), NoPause).RunAsync(CancellationToken.None);

        Assert.Equal(new TaxiRouteBackfillResult(0, 0, 0, 0), second);
        Assert.Equal(snapshot, store.Rows.Values.Select(r => (r.Id, r.FromCityKey, r.ToCityKey)).ToList());
    }

    [Fact]
    public async Task Unresolved_routes_are_reported_not_guessed()
    {
        var store = new MemoryStore();
        store.Add((Haldwani.Latitude, Haldwani.Longitude), (Bhimtal.Latitude, Bhimtal.Longitude));
        store.Add(Delhi, (Bhimtal.Latitude, Bhimtal.Longitude));
        var log = new RecordingLogger();

        // Geocoder down: every nearest-city answer is a guess, so nothing is written.
        var down = await new TaxiRouteCityBackfillPass(store, Resolver(geocoderDown: true), log, NoPause).RunAsync(CancellationToken.None);
        Assert.Equal(2, down.Unresolved);
        Assert.Equal(0, down.Written);
        Assert.All(store.Rows.Values, r => Assert.True(r.FromCityKey == null && r.ToCityKey == null));
        Assert.Equal(1, log.Count(7101));
        Assert.Contains(log.Entries, e => e.Id.Id == 7101 && e.Message.Contains("2 routes"));

        // Geocoder back: the trusted ends are filled, the Delhi end (outside every city) stays null and is reported again.
        var up = await new TaxiRouteCityBackfillPass(store, Resolver(), log, NoPause).RunAsync(CancellationToken.None);
        Assert.Equal(1, up.Unresolved);
        Assert.Equal(2, up.Written);
        var delhiRow = store.Rows.Values.Single(r => r.FromLatitude == (decimal)Delhi.Lat);
        Assert.Null(delhiRow.FromCityKey);
        Assert.Equal(Bhimtal.CityKey, delhiRow.ToCityKey);
        Assert.Equal(2, log.Count(7101));
        Assert.Contains(log.Entries, e => e.Id.Id == 7101 && e.Message.Contains("1 routes"));
    }

    [Fact]
    public async Task Unresolved_routes_log_one_count_per_pass_not_one_line_per_route()
    {
        var store = new MemoryStore();
        for (var i = 0; i < 230; i++) store.Add(Delhi, (Delhi.Lat + i * 0.01, Delhi.Lng));
        var log = new RecordingLogger();

        var result = await new TaxiRouteCityBackfillPass(store, Resolver(), log, NoPause).RunAsync(CancellationToken.None);

        Assert.Equal(230, result.Unresolved);
        Assert.Equal(1, log.Count(7101));
    }

    [Fact]
    public async Task Lookups_are_paced_one_by_one_and_a_repeated_point_is_not_looked_up_again()
    {
        var store = new MemoryStore();
        store.Add((Haldwani.Latitude, Haldwani.Longitude), (Bhimtal.Latitude, Bhimtal.Longitude));
        store.Add((Haldwani.Latitude, Haldwani.Longitude), (Bhimtal.Latitude, Bhimtal.Longitude));
        store.Add((Bhimtal.Latitude, Bhimtal.Longitude), Delhi);
        var gaps = new List<TimeSpan>();

        await new TaxiRouteCityBackfillPass(store, Resolver(), new RecordingLogger(), (gap, _) => { gaps.Add(gap); return Task.CompletedTask; })
            .RunAsync(CancellationToken.None);

        // Three distinct points: Haldwani, Bhimtal, Delhi. A pause before every lookup but the first, none for cache hits.
        Assert.Equal([TaxiRouteCityBackfillPass.LookupPause, TaxiRouteCityBackfillPass.LookupPause], gaps);
    }

    [Fact]
    public async Task Point_cache_counts_only_real_lookups()
    {
        var calls = 0;
        var cache = new TaxiRouteCityKeys.PointCache(Resolver(), _ => { calls++; return Task.CompletedTask; });
        await cache.KeyOfAsync((decimal)Haldwani.Latitude, (decimal)Haldwani.Longitude);
        await cache.KeyOfAsync((decimal)Haldwani.Latitude, (decimal)Haldwani.Longitude);
        await cache.KeyOfAsync((decimal)Bhimtal.Latitude, (decimal)Bhimtal.Longitude);
        Assert.Equal(2, cache.Lookups);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Loop_survives_a_failing_batch()
    {
        var store = new MemoryStore { FailWrite = call => call == 1 };
        for (var i = 0; i < 150; i++) store.Add((Haldwani.Latitude, Haldwani.Longitude), (Bhimtal.Latitude, Bhimtal.Longitude));

        var result = await new TaxiRouteCityBackfillPass(store, Resolver(), new RecordingLogger(), NoPause).RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Batches);
        Assert.Equal(1, result.FailedBatches);
        Assert.Equal(50, result.Written);
        Assert.Equal(100, store.Rows.Values.Count(r => r.FromCityKey == null));

        // The next run picks up the failed batch.
        var next = await new TaxiRouteCityBackfillPass(store, Resolver(), new RecordingLogger(), NoPause).RunAsync(CancellationToken.None);
        Assert.Equal(100, next.Written);
        Assert.All(store.Rows.Values, r => Assert.NotNull(r.FromCityKey));
    }

    [Fact]
    public async Task A_failing_run_never_throws_out_of_the_service()
    {
        var log = new RecordingLogger();
        Assert.Null(await TaxiRouteCityBackfillService.RunGuardedAsync(
            _ => throw new InvalidOperationException("no database"), _ => throw new InvalidOperationException("unreached"), log, CancellationToken.None));
        Assert.Null(await TaxiRouteCityBackfillService.RunGuardedAsync(
            _ => Task.FromResult<IAsyncDisposable?>(new Gate()), _ => throw new TimeoutException(), log, CancellationToken.None));
        Assert.Null(await TaxiRouteCityBackfillService.RunGuardedAsync(
            _ => Task.FromResult<IAsyncDisposable?>(null), _ => throw new InvalidOperationException("unreached"), log, CancellationToken.None));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(await TaxiRouteCityBackfillService.RunGuardedAsync(
            _ => Task.FromResult<IAsyncDisposable?>(new Gate()), ct => Task.FromCanceled<TaxiRouteBackfillResult>(ct), log, cts.Token));
    }

    [Fact]
    public async Task A_read_failure_ends_the_pass_without_throwing()
    {
        var store = new ThrowingReadStore();
        var result = await new TaxiRouteCityBackfillPass(store, Resolver(), new RecordingLogger(), NoPause).RunAsync(CancellationToken.None);
        Assert.Equal(new TaxiRouteBackfillResult(0, 0, 0, 1), result);
    }

    [Theory]
    [InlineData("2026-11-18T18:49:00Z", "2026-11-18T18:50:00Z")]
    [InlineData("2026-11-18T18:50:00Z", "2026-11-19T18:50:00Z")]
    [InlineData("2026-11-18T06:00:00Z", "2026-11-18T18:50:00Z")]
    public void Nightly_run_is_at_00_20_IST(string now, string expected)
    {
        var at = TaxiRouteCityBackfillService.NextNightlyUtc(DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        Assert.Equal(DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal), at);
    }

    private sealed class Gate : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingReadStore : ITaxiRouteCityKeyStore
    {
        public Task<IReadOnlyList<TaxiRouteKeyRow>> NextMissingAsync(Guid after, int take, CancellationToken ct) => throw new InvalidOperationException("db down");
        public Task<int> WriteAsync(IReadOnlyList<TaxiRouteKeyWrite> writes, CancellationToken ct) => Task.FromResult(0);
    }
}
