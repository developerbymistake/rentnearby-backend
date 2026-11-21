using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
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
    public Guid TaxiListingId { get; set; }
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
    // O12 follow-up of a slow lookup at route save: the ringing and accepted offers of each driver are checked against his
    // routes as stored now; a null key at a point whose lookup is still unsettled keeps them. One unit per driver.
    Task<int> RecheckOffersAsync(IReadOnlyCollection<Guid> taxiListingIds, Func<decimal, decimal, bool> unsettled, CancellationToken ct);
}

// Settled = the answer is final: a trusted city, or a point outside every live city. Unsettled = no city list, a guessed
// city (geocoder failed), an error or no answer in time.
public readonly record struct CityLookup(long? Key, bool Settled)
{
    public static readonly CityLookup NoAnswer = new(null, false);
}

// The ONLY writer of TaxiRoute.FromCityKey / ToCityKey. A key the driver picked from the city list is stored as sent once
// CheckPicksAsync passed. Old builds: a key is stored only for an available, found and trusted city; anything else stays
// null (EventId 7101), never a guess.
public static class TaxiRouteCityKeys
{
    public static readonly EventId Unresolved = new(7101, "taxi.route.city.unresolved");

    public static async Task<long?> KeyOfAsync(ICityResolver resolver, double lat, double lng)
        => (await LookupAsync(resolver, lat, lng)).Key;

    public static async Task<CityLookup> LookupAsync(ICityResolver resolver, double lat, double lng)
    {
        try
        {
            var pin = await resolver.ResolveTrustedAsync(lat, lng);
            if (pin is { Available: true, City: { } city, Trusted: true }) return new CityLookup(city.CityKey, true);
            return new CityLookup(null, pin is { Available: true, City: null });
        }
        catch
        {
            return CityLookup.NoAnswer;
        }
    }

    // One resolution per rounded point (about 1 m) for the life of one backfill pass.
    // beforeLookup runs before every real lookup but the first, so the geocoder is paced per call, not per batch.
    public sealed class PointCache(ICityResolver resolver, Func<CancellationToken, Task>? beforeLookup = null)
    {
        private readonly Dictionary<(decimal, decimal), CityLookup> _keys = new();
        private bool _looked;

        public int Lookups { get; private set; }

        public async Task<long?> KeyOfAsync(decimal lat, decimal lng, CancellationToken ct = default)
            => (await LookupAsync(lat, lng, ct)).Key;

        public async Task<CityLookup> LookupAsync(decimal lat, decimal lng, CancellationToken ct = default)
        {
            var at = At(lat, lng);
            if (_keys.TryGetValue(at, out var hit)) return hit;
            if (_looked && beforeLookup != null) await beforeLookup(ct);
            _looked = true;
            Lookups++;
            var lookup = await TaxiRouteCityKeys.LookupAsync(resolver, (double)lat, (double)lng);
            _keys[at] = lookup;
            return lookup;
        }

        // Only a point this pass got a final answer for is settled; a point never looked up is not.
        public bool IsSettled(decimal lat, decimal lng) => _keys.TryGetValue(At(lat, lng), out var hit) && hit.Settled;

        private static (decimal, decimal) At(decimal lat, decimal lng) => (Math.Round(lat, 5), Math.Round(lng, 5));
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
        => (await ResolveForSaveAsync(resolver, routes, logger, budget)).Unresolved;

    // Same as ResolveAsync, plus the keys of every route (in order) for the O12 offer check: an end is pending when its
    // point is new or moved and its lookup gave no final answer inside the budget.
    public static Task<(int Unresolved, IReadOnlyList<RouteServeKeys> Keys)> ResolveForSaveAsync(
        ICityResolver resolver, IReadOnlyList<(TaxiRoute Route, RouteEndsBefore? Before)> routes, ILogger logger, TimeSpan? budget = null)
        => ResolveForSaveAsync(resolver, routes, new RouteCityPick?[routes.Count], logger, budget);

    // picks[i] set = the driver chose both ends of routes[i] from the city list (already checked by CheckPicksAsync):
    // the keys are stored as sent and final, nothing is looked up. null = an old build, the pins decide.
    public static async Task<(int Unresolved, IReadOnlyList<RouteServeKeys> Keys)> ResolveForSaveAsync(
        ICityResolver resolver, IReadOnlyList<(TaxiRoute Route, RouteEndsBefore? Before)> routes, IReadOnlyList<RouteCityPick?> picks,
        ILogger logger, TimeSpan? budget = null)
    {
        if (picks.Count != routes.Count) throw new ArgumentException("One pick slot per route", nameof(picks));
        var points = new HashSet<(decimal Lat, decimal Lng)>();
        for (var i = 0; i < routes.Count; i++)
        {
            var (route, before) = routes[i];
            if (picks[i] != null) continue;
            if (!Keeps(route.FromLatitude, route.FromLongitude, before?.FromLat, before?.FromLng, before?.FromKey))
                points.Add((route.FromLatitude, route.FromLongitude));
            if (!Keeps(route.ToLatitude, route.ToLongitude, before?.ToLat, before?.ToLng, before?.ToKey))
                points.Add((route.ToLatitude, route.ToLongitude));
        }
        var lookup = Task.WhenAll(points.Select(async p => (p, Answer: await LookupAsync(resolver, (double)p.Lat, (double)p.Lng))));
        var answered = await WithinAsync(lookup, budget);
        var answers = answered?.ToDictionary(x => x.p, x => x.Answer) ?? new Dictionary<(decimal Lat, decimal Lng), CityLookup>();

        var unresolved = 0;
        var keys = new List<RouteServeKeys>(routes.Count);
        for (var i = 0; i < routes.Count; i++)
        {
            var (route, before) = routes[i];
            if (picks[i] is { } pick)
            {
                route.FromCityKey = pick.FromKey;
                route.ToCityKey = pick.ToKey;
                route.CityKeysChosen = true;
                keys.Add(new RouteServeKeys(pick.FromKey, pick.ToKey, FromPending: false, ToPending: false));
                continue;
            }
            route.CityKeysChosen = false;
            var from = Keeps(route.FromLatitude, route.FromLongitude, before?.FromLat, before?.FromLng, before?.FromKey)
                ? new CityLookup(before!.FromKey, true) : answers.GetValueOrDefault((route.FromLatitude, route.FromLongitude), CityLookup.NoAnswer);
            var to = Keeps(route.ToLatitude, route.ToLongitude, before?.ToLat, before?.ToLng, before?.ToKey)
                ? new CityLookup(before!.ToKey, true) : answers.GetValueOrDefault((route.ToLatitude, route.ToLongitude), CityLookup.NoAnswer);
            route.FromCityKey = from.Key;
            route.ToCityKey = to.Key;
            keys.Add(new RouteServeKeys(from.Key, to.Key,
                FromPending: from.Key is null && !from.Settled && Moved(route.FromLatitude, route.FromLongitude, before?.FromLat, before?.FromLng),
                ToPending: to.Key is null && !to.Settled && Moved(route.ToLatitude, route.ToLongitude, before?.ToLat, before?.ToLng)));
            if (route.FromCityKey != null && route.ToCityKey != null) continue;
            unresolved++;
            logger.LogWarning(Unresolved, "taxi.route.city.unresolved {RouteId}", route.Id);
        }
        return (unresolved, keys);
    }

    // Every sent key that is new for its slot must be a live city of the catalog (the list of GET /trips/cities) with the
    // pin near its centre. One catalog read per distinct key; the first refusal wins. Before = the slot's route as stored.
    public static async Task<TaxiRouteCityError?> CheckPicksAsync(
        ITripCityCatalog catalog, IReadOnlyList<(TaxiRouteInput Input, RouteCityPick? Pick, RouteEndsBefore? Before)> routes, CancellationToken ct = default)
    {
        var found = new Dictionary<long, (bool Available, TripCityInfo? City)>();
        foreach (var (input, pick, before) in routes)
        {
            if (pick is null) continue;
            var ends = new[] { (pick.FromKey, before?.FromKey, input.FromLatitude, input.FromLongitude), (pick.ToKey, before?.ToKey, input.ToLatitude, input.ToLongitude) };
            foreach (var (key, keyBefore, lat, lng) in ends)
            {
                if (key is not { } k || !TaxiRouteCityPicks.NeedsCatalogCheck(k, keyBefore)) continue;
                if (!found.TryGetValue(k, out var answer))
                {
                    try { answer = await catalog.FindAsync(k, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { answer = (false, null); }
                    found[k] = answer;
                }
                if (TaxiRouteCityPicks.CheckEnd(lat, lng, answer.Available, answer.City) is { } error) return error;
            }
        }
        return null;
    }

    private static bool Keeps(decimal lat, decimal lng, decimal? beforeLat, decimal? beforeLng, long? beforeKey)
        => beforeKey != null && beforeLat == lat && beforeLng == lng;

    // A new route counts as moved: the slot may now hold another route's ends.
    private static bool Moved(decimal lat, decimal lng, decimal? beforeLat, decimal? beforeLng)
        => beforeLat != lat || beforeLng != lng;

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
public sealed class TaxiRouteCityKeyStore(IServiceScopeFactory scopes, ILogger<TaxiRouteCityKeyStore> logger) : ITaxiRouteCityKeyStore
{
    public async Task<IReadOnlyList<TaxiRouteKeyRow>> NextMissingAsync(Guid after, int take, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Database.SqlQuery<TaxiRouteKeyRow>(FormattableStringFactory.Create(NextMissingSql, after, take)).ToListAsync(ct);
    }

    // Routes whose keys the driver chose are final: a null key there is an other place, never guessed.
    public const string NextMissingSql = """
        SELECT "Id", "TaxiListingId", "FromLatitude", "FromLongitude", "ToLatitude", "ToLongitude", "FromCityKey", "ToCityKey"
        FROM "TaxiRoutes"
        WHERE ("FromCityKey" IS NULL OR "ToCityKey" IS NULL) AND NOT "CityKeysChosen" AND "Id" > {0}
        ORDER BY "Id"
        LIMIT {1}
        """;

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
                .IgnoreQueryFilters()
                .Where(x => x.Id == r.Id && !x.CityKeysChosen
                    && x.FromLatitude == r.FromLatitude && x.FromLongitude == r.FromLongitude
                    && x.ToLatitude == r.ToLatitude && x.ToLongitude == r.ToLongitude)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.FromCityKey, x => x.FromCityKey ?? from)
                    .SetProperty(x => x.ToCityKey, x => x.ToCityKey ?? to), ct);
        }
        await tx.CommitAsync(ct);
        return changed;
    }

    // A failed driver is logged and left; the others still run.
    public async Task<int> RecheckOffersAsync(IReadOnlyCollection<Guid> taxiListingIds, Func<decimal, decimal, bool> unsettled, CancellationToken ct)
    {
        var done = 0;
        foreach (var listingId in taxiListingIds.Order())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var closure = scope.ServiceProvider.GetRequiredService<TaxiRequestClosure>();
                var signals = await PgRetry.OnceAsync(db, async c =>
                {
                    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, c);
                    var routes = await db.TaxiRoutes.AsNoTracking().Where(r => r.TaxiListingId == listingId)
                        .Select(r => new { r.FromLatitude, r.FromLongitude, r.FromCityKey, r.ToLatitude, r.ToLongitude, r.ToCityKey })
                        .ToListAsync(c);
                    var keys = routes.Select(r => new RouteServeKeys(r.FromCityKey, r.ToCityKey,
                        FromPending: r.FromCityKey is null && unsettled(r.FromLatitude, r.FromLongitude),
                        ToPending: r.ToCityKey is null && unsettled(r.ToLatitude, r.ToLongitude))).ToList();
                    var result = await closure.RoutesChangedAsync(db, listingId, keys, c);
                    await db.SaveChangesAsync(c);
                    await tx.CommitAsync(c);
                    return result;
                }, ct, logger);
                await closure.AfterCommitAsync(signals, poolChanged: true);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "taxi.route.city.backfill offer recheck failed for {TaxiListingId}", listingId);
            }
        }
        return done;
    }
}
