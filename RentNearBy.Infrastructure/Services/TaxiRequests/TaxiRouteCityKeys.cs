using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// The ends of a route as they were before an edit; null for a new route.
public sealed record RouteEndsBefore(decimal FromLat, decimal FromLng, long? FromKey, decimal ToLat, decimal ToLng, long? ToKey)
{
    public static RouteEndsBefore? Of(TaxiRoute? r)
        => r == null ? null : new RouteEndsBefore(r.FromLatitude, r.FromLongitude, r.FromCityKey, r.ToLatitude, r.ToLongitude, r.ToCityKey);
}

public sealed class TaxiRouteKeyRow
{
    public Guid Id { get; set; }
    public decimal FromLatitude { get; set; }
    public decimal FromLongitude { get; set; }
    public decimal ToLatitude { get; set; }
    public decimal ToLongitude { get; set; }
    public long? FromCityKey { get; set; }
    public long? ToCityKey { get; set; }
}

public sealed record TaxiRouteKeyWrite(TaxiRouteKeyRow Row, long? FromCityKey, long? ToCityKey);

public interface ITaxiRouteCityKeyStore
{
    // Routes with at least one null key and Id > after, ordered by Id.
    Task<IReadOnlyList<TaxiRouteKeyRow>> NextMissingAsync(Guid after, int take, CancellationToken ct);
    // Fills only keys that are still null on rows whose ends did not move since they were read; one transaction.
    Task<int> WriteAsync(IReadOnlyList<TaxiRouteKeyWrite> writes, CancellationToken ct);
}

// The ONLY writer of TaxiRoute.FromCityKey / ToCityKey. A key is stored only for an available, found and trusted city;
// anything else stays null (EventId 7101), never a guess. A later Atlas picker adds one branch here: a valid sent key is used.
public static class TaxiRouteCityKeys
{
    public static readonly EventId Unresolved = new(7101, "taxi.route.city.unresolved");

    public static async Task<long?> KeyOfAsync(ICityResolver resolver, double lat, double lng)
    {
        try
        {
            var pin = await resolver.ResolveTrustedAsync(lat, lng);
            return pin is { Available: true, City: { } city, Trusted: true } ? city.CityKey : null;
        }
        catch
        {
            return null;
        }
    }

    // One resolution per rounded point (about 1 m) for the life of one backfill pass.
    // beforeLookup runs before every real lookup but the first, so the geocoder is paced per call, not per batch.
    public sealed class PointCache(ICityResolver resolver, Func<CancellationToken, Task>? beforeLookup = null)
    {
        private readonly Dictionary<(decimal, decimal), long?> _keys = new();
        private bool _looked;

        public int Lookups { get; private set; }

        public async Task<long?> KeyOfAsync(decimal lat, decimal lng, CancellationToken ct = default)
        {
            var at = (Math.Round(lat, 5), Math.Round(lng, 5));
            if (_keys.TryGetValue(at, out var hit)) return hit;
            if (_looked && beforeLookup != null) await beforeLookup(ct);
            _looked = true;
            Lookups++;
            var key = await TaxiRouteCityKeys.KeyOfAsync(resolver, (double)lat, (double)lng);
            _keys[at] = key;
            return key;
        }
    }

    // A request path waits at most this long for city lookups (a cold city grid or a slow geocoder); what is not
    // back by then stays null and the backfill fills it.
    public static readonly TimeSpan RequestBudget = TimeSpan.FromMilliseconds(1500);

    // Null when the lookup is not done inside the budget. The lookup keeps running alone and touches nothing of the caller.
    // A zero budget takes only an answer that is already there (a warm city grid) and never waits.
    private static async Task<T?> WithinAsync<T>(Task<T> lookup, TimeSpan? budget) where T : class
    {
        if (budget is not { } limit) return await lookup;
        if (limit <= TimeSpan.Zero) return lookup.IsCompletedSuccessfully ? lookup.Result : null;
        return await Task.WhenAny(lookup, Task.Delay(limit)) == lookup ? await lookup : null;
    }

    // Before SaveChanges and outside any transaction. An end keeps its key when its point did not move; else it is resolved again.
    // The points to resolve are looked up together, so a slow geocoder costs one timeout, not one per end.
    // budget: null waits for every lookup; else unanswered points stay null after it.
    public static async Task<int> ResolveAsync(
        ICityResolver resolver, IReadOnlyList<(TaxiRoute Route, RouteEndsBefore? Before)> routes, ILogger logger, TimeSpan? budget = null)
    {
        var points = new HashSet<(decimal Lat, decimal Lng)>();
        foreach (var (route, before) in routes)
        {
            if (!Keeps(route.FromLatitude, route.FromLongitude, before?.FromLat, before?.FromLng, before?.FromKey))
                points.Add((route.FromLatitude, route.FromLongitude));
            if (!Keeps(route.ToLatitude, route.ToLongitude, before?.ToLat, before?.ToLng, before?.ToKey))
                points.Add((route.ToLatitude, route.ToLongitude));
        }
        var lookup = Task.WhenAll(points.Select(async p => (p, Key: await KeyOfAsync(resolver, (double)p.Lat, (double)p.Lng))));
        var answered = await WithinAsync(lookup, budget);
        var keys = answered?.ToDictionary(x => x.p, x => x.Key) ?? new Dictionary<(decimal Lat, decimal Lng), long?>();

        var unresolved = 0;
        foreach (var (route, before) in routes)
        {
            route.FromCityKey = Keeps(route.FromLatitude, route.FromLongitude, before?.FromLat, before?.FromLng, before?.FromKey)
                ? before!.FromKey : keys.GetValueOrDefault((route.FromLatitude, route.FromLongitude));
            route.ToCityKey = Keeps(route.ToLatitude, route.ToLongitude, before?.ToLat, before?.ToLng, before?.ToKey)
                ? before!.ToKey : keys.GetValueOrDefault((route.ToLatitude, route.ToLongitude));
            if (route.FromCityKey != null && route.ToCityKey != null) continue;
            unresolved++;
            logger.LogWarning(Unresolved, "taxi.route.city.unresolved {RouteId}", route.Id);
        }
        return unresolved;
    }

    private static bool Keeps(decimal lat, decimal lng, decimal? beforeLat, decimal? beforeLng, long? beforeKey)
        => beforeKey != null && beforeLat == lat && beforeLng == lng;

    // The save is the authority for both keys: they are written even when EF sees no change against the loaded value,
    // so a backfill write that landed between the load and this save (a key for the old point) is overwritten.
    public static void MarkKeysWritten(DbContext db, IEnumerable<TaxiRoute> routes)
    {
        foreach (var route in routes)
        {
            var entry = db.Entry(route);
            if (entry.State is not (EntityState.Unchanged or EntityState.Modified)) continue;
            entry.Property(r => r.FromCityKey).IsModified = true;
            entry.Property(r => r.ToCityKey).IsModified = true;
        }
    }

    // Backfill: the keys a batch of rows can take; a row with nothing new is left out. Unresolved rows are only counted;
    // the pass logs one total.
    public static async Task<(List<TaxiRouteKeyWrite> Writes, int Unresolved)> ResolveRowsAsync(
        PointCache cache, IReadOnlyList<TaxiRouteKeyRow> rows, CancellationToken ct = default)
    {
        var writes = new List<TaxiRouteKeyWrite>();
        var unresolved = 0;
        foreach (var row in rows)
        {
            var from = row.FromCityKey ?? await cache.KeyOfAsync(row.FromLatitude, row.FromLongitude, ct);
            var to = row.ToCityKey ?? await cache.KeyOfAsync(row.ToLatitude, row.ToLongitude, ct);
            if (from != row.FromCityKey || to != row.ToCityKey) writes.Add(new TaxiRouteKeyWrite(row, from, to));
            if (from == null || to == null) unresolved++;
        }
        return (writes, unresolved);
    }

    // City names for the keys on route DTOs; a key that is not a live city right now, or not looked up inside the budget, keeps a null name.
    public static async Task FillNamesAsync(IEnumerable<TaxiRouteDto> routes, ICityResolver resolver, TimeSpan? budget = null)
    {
        var list = routes.ToList();
        var lookup = Task.WhenAll(list.SelectMany(r => new[] { r.FromCityKey, r.ToCityKey }).OfType<long>().Distinct()
            .Select(async key =>
            {
                try { return (key, Name: (await resolver.FindAsync(key)).City?.Name); }
                catch { return (key, Name: (string?)null); }
            }));
        var names = (await WithinAsync(lookup, budget))?.ToDictionary(x => x.key, x => x.Name) ?? new Dictionary<long, string?>();
        foreach (var r in list)
        {
            r.FromCityName = r.FromCityKey is { } f ? names.GetValueOrDefault(f) : null;
            r.ToCityName = r.ToCityKey is { } t ? names.GetValueOrDefault(t) : null;
        }
    }
}

// Each call opens its own scope, so one batch is one short unit of work.
public sealed class TaxiRouteCityKeyStore(IServiceScopeFactory scopes) : ITaxiRouteCityKeyStore
{
    public async Task<IReadOnlyList<TaxiRouteKeyRow>> NextMissingAsync(Guid after, int take, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Database.SqlQuery<TaxiRouteKeyRow>($"""
            SELECT "Id", "FromLatitude", "FromLongitude", "ToLatitude", "ToLongitude", "FromCityKey", "ToCityKey"
            FROM "TaxiRoutes"
            WHERE ("FromCityKey" IS NULL OR "ToCityKey" IS NULL) AND "Id" > {after}
            ORDER BY "Id"
            LIMIT {take}
            """).ToListAsync(ct);
    }

    public async Task<int> WriteAsync(IReadOnlyList<TaxiRouteKeyWrite> writes, CancellationToken ct)
    {
        if (writes.Count == 0) return 0;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = 0;
        foreach (var w in writes)
        {
            var r = w.Row;
            var from = w.FromCityKey;
            var to = w.ToCityKey;
            changed += await db.TaxiRoutes
                .Where(x => x.Id == r.Id
                    && x.FromLatitude == r.FromLatitude && x.FromLongitude == r.FromLongitude
                    && x.ToLatitude == r.ToLatitude && x.ToLongitude == r.ToLongitude)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.FromCityKey, x => x.FromCityKey ?? from)
                    .SetProperty(x => x.ToCityKey, x => x.ToCityKey ?? to), ct);
        }
        await tx.CommitAsync(ct);
        return changed;
    }
}
