using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

internal sealed class RecordingLogger : ILogger
{
    public readonly List<(EventId Id, string Message)> Entries = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((eventId, formatter(state, exception)));
    }
    public int Count(int eventId) { lock (Entries) return Entries.Count(e => e.Id.Id == eventId); }
}

public class TaxiRouteCityKeyTests
{
    private static TripCityInfo City(long key, string name, double lat, double lng)
        => new() { CityKey = key, Name = name, DistrictName = "Nainital", StateName = "Uttarakhand", Latitude = lat, Longitude = lng };

    internal static readonly TripCityInfo Haldwani = City(210000101, "Haldwani", 29.2183, 79.5130);
    internal static readonly TripCityInfo Kathgodam = City(210000102, "Kathgodam", 29.2656, 79.5262);
    internal static readonly TripCityInfo Bhimtal = City(210000103, "Bhimtal", 29.3470, 79.5640);

    private sealed class Net(TaxiWaysNetworkSnapshot? s) : ITaxiWaysNetwork
    {
        public Task<TaxiWaysNetworkSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult(s);
        public string? CurrentVersion => "t";
    }

    internal sealed class FakeGeocoder : IAreaGeocoder
    {
        public bool Down;
        public Task<AreaAddress?> ReverseAreaAsync(double lat, double lng, CancellationToken ct = default)
        {
            if (Down) return Task.FromResult<AreaAddress?>(null);
            var near = new[] { Haldwani, Kathgodam, Bhimtal }
                .OrderBy(c => TaxiGeo.DistanceMeters(lat, lng, c.Latitude, c.Longitude)).First();
            var name = near == Kathgodam ? "Haldwani" : near.Name;
            return Task.FromResult<AreaAddress?>(new AreaAddress(name, null, null, null));
        }
    }

    // The real resolver chain the app uses: CityResolver over ParentCityResolver over the cached city grid.
    internal static ICityResolver Resolver(FakeGeocoder geo, bool atlasUp = true)
    {
        var snapshot = atlasUp ? TaxiWaysClustering.Build([], [Haldwani, Kathgodam, Bhimtal], new TaxiWaysOptions(), "t", DateTime.UtcNow) : null;
        var net = new Net(snapshot);
        var options = Options.Create(new TaxiWaysOptions());
        var parent = new ParentCityResolver(net, options, geo, new CityResolveCache(new MemoryCache(new MemoryCacheOptions())));
        return new CityResolver(net, options, parent);
    }

    private static TaxiRoute Route((double Lat, double Lng) from, (double Lat, double Lng) to) => new()
    {
        Id = Guid.NewGuid(),
        FromLatitude = (decimal)from.Lat, FromLongitude = (decimal)from.Lng,
        ToLatitude = (decimal)to.Lat, ToLongitude = (decimal)to.Lng,
    };

    private static Task<int> Resolve(ICityResolver resolver, RecordingLogger log, params TaxiRoute[] routes)
        => TaxiRouteCityKeys.ResolveAsync(resolver, routes.Select(r => (r, (RouteEndsBefore?)null)).ToList(), log);

    [Fact]
    public async Task Trusted_city_is_stored()
    {
        var log = new RecordingLogger();
        var route = Route((Kathgodam.Latitude, Kathgodam.Longitude), (Bhimtal.Latitude, Bhimtal.Longitude));

        var unresolved = await Resolve(Resolver(new FakeGeocoder()), log, route);

        Assert.Equal(0, unresolved);
        Assert.Equal(Haldwani.CityKey, route.FromCityKey);
        Assert.Equal(Bhimtal.CityKey, route.ToCityKey);
        Assert.Equal(0, log.Count(7101));
    }

    [Fact]
    public async Task Geocoder_fallback_is_not_stored()
    {
        var log = new RecordingLogger();
        var resolver = Resolver(new FakeGeocoder { Down = true });
        var route = Route((Kathgodam.Latitude, Kathgodam.Longitude), (Bhimtal.Latitude, Bhimtal.Longitude));

        // The nearest-city guess exists, but it is not trusted.
        var pin = await resolver.ResolveTrustedAsync(Kathgodam.Latitude, Kathgodam.Longitude);
        Assert.NotNull(pin.City);
        Assert.False(pin.Trusted);

        await Resolve(resolver, log, route);

        Assert.Null(route.FromCityKey);
        Assert.Null(route.ToCityKey);
        Assert.Equal(1, log.Count(7101));
        Assert.Contains(log.Entries, e => e.Id.Id == 7101 && e.Message.Contains(route.Id.ToString()));
    }

    [Fact]
    public async Task Atlas_unavailable_leaves_null()
    {
        var log = new RecordingLogger();
        var route = Route((Kathgodam.Latitude, Kathgodam.Longitude), (Bhimtal.Latitude, Bhimtal.Longitude));

        await Resolve(Resolver(new FakeGeocoder(), atlasUp: false), log, route);

        Assert.Null(route.FromCityKey);
        Assert.Null(route.ToCityKey);
        Assert.Equal(1, log.Count(7101));
    }

    [Fact]
    public async Task Outside_every_city_leaves_null()
    {
        var log = new RecordingLogger();
        var delhi = (28.6139, 77.2090);
        var route = Route(delhi, (Bhimtal.Latitude, Bhimtal.Longitude));

        await Resolve(Resolver(new FakeGeocoder()), log, route);

        Assert.Null(route.FromCityKey);
        Assert.Equal(Bhimtal.CityKey, route.ToCityKey);
        Assert.Equal(1, log.Count(7101));
    }

    private sealed class CountingResolver(Func<double, double, TrustedCityPin> answer) : ICityResolver
    {
        public readonly List<(double Lat, double Lng)> Calls = [];
        public double BoxKm => 10;
        public Task<CityPin> ResolveAsync(double lat, double lng) => throw new InvalidOperationException("use ResolveTrustedAsync");
        public Task<TrustedCityPin> ResolveTrustedAsync(double lat, double lng)
        {
            lock (Calls) Calls.Add((lat, lng));
            return Task.FromResult(answer(lat, lng));
        }
        public Task<CityPin> FindAsync(long cityKey) => Task.FromResult(new CityPin(true, null));
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey)
            => Task.FromResult<IReadOnlyList<T>>([]);
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    [Fact]
    public async Task Unchanged_end_is_not_resolved_again()
    {
        var resolver = new CountingResolver((_, _) => new TrustedCityPin(true, Bhimtal, true));
        var route = Route((Kathgodam.Latitude, Kathgodam.Longitude), (Haldwani.Latitude, Haldwani.Longitude));
        route.FromCityKey = Haldwani.CityKey;
        route.ToCityKey = Haldwani.CityKey;
        var before = RouteEndsBefore.Of(route);
        route.ToLatitude = (decimal)Bhimtal.Latitude;
        route.ToLongitude = (decimal)Bhimtal.Longitude;

        await TaxiRouteCityKeys.ResolveAsync(resolver, [(route, before)], new RecordingLogger());

        Assert.Single(resolver.Calls);
        Assert.Equal((Bhimtal.Latitude, Bhimtal.Longitude), resolver.Calls[0]);
        Assert.Equal(Haldwani.CityKey, route.FromCityKey);
        Assert.Equal(Bhimtal.CityKey, route.ToCityKey);
    }

    [Fact]
    public async Task Unchanged_end_with_no_key_is_resolved_again()
    {
        var resolver = new CountingResolver((_, _) => new TrustedCityPin(true, Haldwani, true));
        var route = Route((Haldwani.Latitude, Haldwani.Longitude), (Haldwani.Latitude, Haldwani.Longitude));
        var before = RouteEndsBefore.Of(route);

        await TaxiRouteCityKeys.ResolveAsync(resolver, [(route, before)], new RecordingLogger());

        Assert.Single(resolver.Calls);
        Assert.Equal(Haldwani.CityKey, route.FromCityKey);
        Assert.Equal(Haldwani.CityKey, route.ToCityKey);
    }

    [Fact]
    public async Task Moved_end_drops_its_old_key_when_the_new_city_is_not_trusted()
    {
        var resolver = new CountingResolver((_, _) => new TrustedCityPin(true, Bhimtal, false));
        var route = Route((Haldwani.Latitude, Haldwani.Longitude), (Haldwani.Latitude, Haldwani.Longitude));
        route.FromCityKey = Haldwani.CityKey;
        route.ToCityKey = Haldwani.CityKey;
        var before = RouteEndsBefore.Of(route);
        route.ToLatitude = (decimal)Bhimtal.Latitude;

        await TaxiRouteCityKeys.ResolveAsync(resolver, [(route, before)], new RecordingLogger());

        Assert.Equal(Haldwani.CityKey, route.FromCityKey);
        Assert.Null(route.ToCityKey);
    }

    [Fact]
    public async Task Resolver_failure_leaves_null_and_never_throws()
    {
        var resolver = new CountingResolver((_, _) => throw new TimeoutException());
        var route = Route((Haldwani.Latitude, Haldwani.Longitude), (Bhimtal.Latitude, Bhimtal.Longitude));
        var log = new RecordingLogger();

        await TaxiRouteCityKeys.ResolveAsync(resolver, [(route, null)], log);

        Assert.Null(route.FromCityKey);
        Assert.Null(route.ToCityKey);
        Assert.Equal(1, log.Count(7101));
    }

    // Answers only when released: a cold city grid or a stuck geocoder.
    private sealed class HangingResolver : ICityResolver
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public double BoxKm => 10;
        public Task<CityPin> ResolveAsync(double lat, double lng) => throw new InvalidOperationException("use ResolveTrustedAsync");
        public async Task<TrustedCityPin> ResolveTrustedAsync(double lat, double lng)
        {
            await Release.Task;
            return new TrustedCityPin(true, Haldwani, true);
        }
        public async Task<CityPin> FindAsync(long cityKey)
        {
            await Release.Task;
            return new CityPin(true, Haldwani);
        }
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey)
            => Task.FromResult<IReadOnlyList<T>>([]);
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    [Fact]
    public async Task Save_does_not_wait_past_the_budget_and_a_late_answer_changes_nothing()
    {
        var resolver = new HangingResolver();
        var route = Route((Haldwani.Latitude, Haldwani.Longitude), (Bhimtal.Latitude, Bhimtal.Longitude));
        var log = new RecordingLogger();

        var unresolved = await TaxiRouteCityKeys.ResolveAsync(resolver, [(route, null)], log, TimeSpan.FromMilliseconds(50))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, unresolved);
        Assert.Null(route.FromCityKey);
        Assert.Null(route.ToCityKey);
        Assert.Equal(1, log.Count(7101));

        resolver.Release.SetResult();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Null(route.FromCityKey);
        Assert.Null(route.ToCityKey);
    }

    [Fact]
    public async Task Save_inside_the_budget_stores_the_keys()
    {
        var resolver = new HangingResolver();
        resolver.Release.SetResult();
        var route = Route((Haldwani.Latitude, Haldwani.Longitude), (Haldwani.Latitude, Haldwani.Longitude));

        await TaxiRouteCityKeys.ResolveAsync(resolver, [(route, null)], new RecordingLogger(), TaxiRouteCityKeys.RequestBudget);

        Assert.Equal(Haldwani.CityKey, route.FromCityKey);
        Assert.Equal(Haldwani.CityKey, route.ToCityKey);
    }

    [Fact]
    public async Task Names_do_not_wait_past_the_budget()
    {
        var resolver = new HangingResolver();
        var dtos = new List<RentNearBy.Core.DTOs.Responses.TaxiRouteDto> { new() { FromCityKey = Haldwani.CityKey, ToCityKey = Haldwani.CityKey } };

        await TaxiRouteCityKeys.FillNamesAsync(dtos, resolver, TimeSpan.FromMilliseconds(50))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Null(dtos[0].FromCityName);
        Assert.Null(dtos[0].ToCityName);

        resolver.Release.SetResult();
        await TaxiRouteCityKeys.FillNamesAsync(dtos, resolver, TaxiRouteCityKeys.RequestBudget);
        Assert.Equal("Haldwani", dtos[0].FromCityName);
        Assert.Equal("Haldwani", dtos[0].ToCityName);
    }

    private sealed class PlainResolver(CityPin pin) : ICityResolver
    {
        public double BoxKm => 10;
        public Task<CityPin> ResolveAsync(double lat, double lng) => Task.FromResult(pin);
        public Task<CityPin> FindAsync(long cityKey) => Task.FromResult(pin);
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey)
            => Task.FromResult<IReadOnlyList<T>>([]);
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    [Fact]
    public async Task Implementer_without_the_new_method_gets_the_default()
    {
        ICityResolver found = new PlainResolver(new CityPin(true, Haldwani));
        ICityResolver none = new PlainResolver(new CityPin(true, null));
        ICityResolver down = new PlainResolver(new CityPin(false, null));

        Assert.Equal(new TrustedCityPin(true, Haldwani, true), await found.ResolveTrustedAsync(1, 1));
        Assert.False((await none.ResolveTrustedAsync(1, 1)).Trusted);
        Assert.False((await down.ResolveTrustedAsync(1, 1)).Trusted);
    }

    [Fact]
    public async Task Zero_budget_names_take_only_a_ready_answer_and_never_wait()
    {
        var cold = new HangingResolver();
        var dtos = new List<RentNearBy.Core.DTOs.Responses.TaxiRouteDto> { new() { FromCityKey = Haldwani.CityKey, ToCityKey = Haldwani.CityKey } };

        var fill = TaxiRouteCityKeys.FillNamesAsync(dtos, cold, TimeSpan.Zero);
        Assert.True(fill.IsCompleted);
        await fill;
        Assert.Null(dtos[0].FromCityName);

        await TaxiRouteCityKeys.FillNamesAsync(dtos, new PlainResolver(new CityPin(true, Haldwani)), TimeSpan.Zero);
        Assert.Equal("Haldwani", dtos[0].FromCityName);
        Assert.Equal("Haldwani", dtos[0].ToCityName);
        cold.Release.SetResult();
    }

    [Fact]
    public async Task Route_dto_names_come_from_live_cities()
    {
        var dtos = new List<RentNearBy.Core.DTOs.Responses.TaxiRouteDto>
        {
            new() { FromCityKey = Haldwani.CityKey, ToCityKey = null },
        };
        await TaxiRouteCityKeys.FillNamesAsync(dtos, new PlainResolver(new CityPin(true, Haldwani)));
        Assert.Equal("Haldwani", dtos[0].FromCityName);
        Assert.Null(dtos[0].ToCityName);

        await TaxiRouteCityKeys.FillNamesAsync(dtos, new PlainResolver(new CityPin(false, null)));
        Assert.Null(dtos[0].FromCityName);
    }

    // A write of a key: "FromCityKey = x" or SetProperty(r => r.FromCityKey, ...), a SQL assignment (SET "FromCityKey" = ...,
    // ", "ToCityKey" = ..." in a SET list, or a line that starts with the bare quoted column), an INSERT INTO "TaxiRoutes" naming
    // a key, or a Mapster map INTO a TaxiRoute (same-name properties would copy a key without naming it).
    // A qualified or WHERE/AND/ON comparison (rt."ToCityKey" = @to) is a read.
    private static readonly Regex Write = new(
        @"\b(From|To)CityKey\b\s*=(?!=)|SetProperty\(\s*\w+\s*=>\s*\w+\.(From|To)CityKey\b"
        + @"|(?:^\s*|(?i:\bSET\b)\s*|,\s*)""(From|To)CityKey""\s*=(?!=)"
        + @"|(?i:\bINSERT\s+INTO\s+)""*TaxiRoutes\b[^;]*""(From|To)CityKey"""
        + @"|\.Adapt<TaxiRoute>|TypeAdapterConfig<\s*\w+\s*,\s*TaxiRoute\s*>|\.Adapt\(\s*\w*[Rr]oute\w*\s*\)", RegexOptions.Compiled);
    // A same-name copy of a stored key into a read model ("FromCityKey = r.FromCityKey" in a DTO projection). Only the read-model
    // files below may do it; anywhere else the same line is a write (for example route.FromCityKey = request.FromCityKey).
    private static readonly Regex Copy = new(@"\b(From|To)CityKey\s*=\s*[\w.]*\.\1CityKey\b", RegexOptions.Compiled);
    private static readonly HashSet<string> ReadModelFiles =
        ["RentNearBy.Api/Mappings/DtoMappings.cs", "RentNearBy.Infrastructure/Services/Owner/Be3/TaxiHubReader.cs"];

    internal static bool IsWrite(string rel, string line)
        => Write.IsMatch(ReadModelFiles.Contains(rel) ? Copy.Replace(line, "") : line);

    internal static IEnumerable<string> Writers(string root)
    {
        foreach (var project in new[] { "RentNearBy.Api", "RentNearBy.Infrastructure" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("/Migrations/")) continue;
            if (rel.EndsWith("/TaxiRequests/TaxiRouteCityKeys.cs", StringComparison.Ordinal)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (IsWrite(rel, lines[i])) yield return $"{rel}:{i + 1}: {lines[i].Trim()}";
        }
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Keys_are_written_only_by_TaxiRouteCityKeys()
    {
        var root = Root();
        Assert.True(File.Exists(Path.Combine(root, "RentNearBy.Infrastructure/Services/TaxiRequests/TaxiRouteCityKeys.cs")));
        Assert.Empty(Writers(root));
    }

    [Fact]
    public void The_guard_sees_every_write_form()
    {
        Assert.Matches(Write, "route.FromCityKey = 5;");
        Assert.Matches(Write, "new TaxiRoute { ToCityKey = key }");
        Assert.Matches(Write, ".SetProperty(r => r.ToCityKey, 7)");
        Assert.Matches(Write, "UPDATE \"TaxiRoutes\" SET \"FromCityKey\" = 1");
        Assert.Matches(Write, "UPDATE \"TaxiRoutes\" SET \"Seats\" = 4, \"ToCityKey\" = @to");
        Assert.Matches(Write, "    \"ToCityKey\" = @to,");
        Assert.Matches(Write, "ON CONFLICT (\"Id\") DO UPDATE SET \"FromCityKey\" = EXCLUDED.\"FromCityKey\"");
        Assert.Matches(Write, "INSERT INTO \"TaxiRoutes\" (\"Id\", \"FromCityKey\", \"ToCityKey\") VALUES");
        Assert.Matches(Write, "INSERT INTO TaxiRoutes (Id, \"ToCityKey\")");
        Assert.DoesNotMatch(Write, "x.FromCityKey == key");
        Assert.DoesNotMatch(Write, "EXISTS (SELECT 1 FROM \"TaxiRoutes\" rt WHERE rt.\"TaxiListingId\" = l.\"Id\" AND rt.\"ToCityKey\" = r.\"MatchToCityKey\"");
        Assert.DoesNotMatch(Write, "AND (r.\"MatchFromCityKey\" IS NULL OR rt.\"FromCityKey\" = r.\"MatchFromCityKey\"))");
        Assert.DoesNotMatch(Write, "WHERE rt.\"ToCityKey\" = @to AND rt.\"FromCityKey\" IS NOT NULL AND rt.\"FromCityKey\" <> rt.\"ToCityKey\"");
        Assert.DoesNotMatch(Write, "WHERE \"FromCityKey\" = @from AND \"ToCityKey\" = @to");
        Assert.DoesNotMatch(Write, "JOIN \"TaxiRequests\" q ON q.\"MatchToCityKey\" = rt.\"ToCityKey\"");
        Assert.DoesNotMatch(Write, "SELECT \"Id\", \"FromCityKey\", \"ToCityKey\" FROM \"TaxiRoutes\"");
        Assert.DoesNotMatch(Write, "INSERT INTO \"TaxiRequests\" (\"Id\", \"MatchToCityKey\") SELECT rt.\"ToCityKey\" FROM \"TaxiRoutes\" rt");
        Assert.True(IsWrite("RentNearBy.Api/Handlers/TaxiHandlers.cs", "route.FromCityKey = request.FromCityKey;"));
        Assert.True(IsWrite("RentNearBy.Api/Handlers/TaxiHandlers.cs", "ToCityKey = r.ToCityKey,"));
        Assert.True(IsWrite("RentNearBy.Api/Mappings/DtoMappings.cs", "route.FromCityKey = 5;"));
        Assert.False(IsWrite("RentNearBy.Api/Mappings/DtoMappings.cs", "FromCityKey = r.FromCityKey,"));
        Assert.True(IsWrite("RentNearBy.Api/Handlers/TaxiHandlers.cs", "var route = input.Adapt<TaxiRoute>();"));
        Assert.True(IsWrite("RentNearBy.Api/Handlers/TaxiHandlers.cs", "input.Adapt(route);"));
        Assert.True(IsWrite("RentNearBy.Api/Mappings/DtoMappings.cs", "TypeAdapterConfig<TaxiRouteInput, TaxiRoute>.NewConfig()"));
        Assert.False(IsWrite("RentNearBy.Api/Handlers/TaxiHandlers.cs", "var dto = taxi.Adapt<TaxiOwnerDto>();"));
    }
}
