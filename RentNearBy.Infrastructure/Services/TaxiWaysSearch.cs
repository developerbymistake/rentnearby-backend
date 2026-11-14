using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Search = network lookup (in memory) + cached plan list + cached destination block. Never calls OSRM.
public class TaxiWaysSearch(
    ITaxiWaysNetwork network,
    IServiceScopeFactory scopeFactory,
    IServiceProvider sp,
    IMemoryCache memory,
    IOptions<TaxiWaysOptions> options,
    IUnitOfWork unitOfWork,
    ILogger<TaxiWaysSearch> logger,
    ICityResolver? resolver = null) : ITaxiWaysSearch
{
    internal sealed class DestBlock
    {
        public List<TaxiBrowseDto> LocalTaxis { get; set; } = new();
        public List<StayNearbyDto> Stays { get; set; } = new();
    }

    public async Task<TaxiWaysResultDto?> SearchAsync(TaxiWaysQuery q, CancellationToken ct = default)
    {
        var o = options.Value;
        var watch = Stopwatch.StartNew();
        var snapshot = await network.GetAsync(ct);
        if (snapshot == null) return null;

        var from = TaxiWaysSearchEngine.Resolve(snapshot, q.FromLat, q.FromLng, q.FromMyLocation, o, null, await PinAsync(q.FromLat, q.FromLng));
        var to = TaxiWaysSearchEngine.Resolve(snapshot, q.ToLat, q.ToLng, false, o, TaxiWaysLabels.Drop, await PinAsync(q.ToLat, q.ToLng));
        var people = Math.Clamp(q.People, 1, o.MaxPeople);
        var redis = sp.GetService<IConnectionMultiplexer>();
        var memoryTtl = TimeSpan.FromSeconds(o.MemoryCacheSeconds);

        var (core, source) = await TaxiWaysPlanCache.GetAsync(
            memory, redis, TaxiWaysKeys.Plan(snapshot.CityGrid.Count == 0 ? snapshot.Version + "~nc" + CacheRules : CacheVersion(snapshot), from.Ref, to.Ref, people),
            TimeSpan.FromMinutes(o.PlanCacheMinutes), memoryTtl,
            () => ComputeCoreAsync(snapshot, from, to, people, o), logger, ct);

        var citiesUnavailable = snapshot.CityGrid.Count == 0;
        var (dest, _) = await TaxiWaysPlanCache.GetAsync(
            memory, redis, TaxiWaysKeys.Dest(CacheVersion(snapshot), to.Key),
            TimeSpan.FromMinutes(o.DestCacheMinutes), memoryTtl,
            () => ComputeDestAsync(snapshot, to, o), logger, ct);

        if (core == null) return null;

        var fromName = from.Resolved ? from.Name : TaxiWaysLabels.Of(q.FromName, from.Key, true, false);
        var toName = to.Resolved ? to.Name : TaxiWaysLabels.Of(q.ToName, to.Key, false, true);
        var result = new TaxiWaysResultDto
        {
            Version = core.Version,
            From = new WayPlaceDto { Key = from.Ref, Name = fromName, Lat = from.Lat, Lng = from.Lng, Resolved = from.Resolved },
            To = new WayPlaceDto { Key = to.Ref, Name = toName, Lat = to.Lat, Lng = to.Lng, Resolved = to.Resolved },
            BestWay = core.BestWay,
            AllTheWay = core.AllTheWay,
            AllTheWayHasMore = core.AllTheWayHasMore,
            Plans = TaxiWaysNameOverlay.Apply(core.Plans, from.Key, from.Resolved ? null : fromName, to.Key, to.Resolved ? null : toName),
            Truncated = core.Truncated,
            LocalTaxis = dest?.LocalTaxis ?? new(),
            Stays = dest?.Stays ?? new(),
            ReachFirstPlace = core.ReachFirstPlace,
            RoadLines = core.RoadLines,
            CitiesUnavailable = citiesUnavailable,
        };

        logger.LogInformation("taxi.ways.search {FromKey} {ToKey} {Cars} {Plans} {Ms} {Cache}",
            from.Key, to.Key, result.Plans.Count == 0 ? (result.AllTheWay.Count > 0 ? 1 : 0) : result.Plans[0].Cars,
            result.Plans.Count, watch.ElapsedMilliseconds, source);
        return result;
    }

    // CacheRules: bumped whenever the shown ways change shape or rule, so cached plans of an older engine are never served.
    private const string CacheRules = "~w3";

    private static string CacheVersion(TaxiWaysNetworkSnapshot s) => (s.ParentResolved ? s.Version + "~p" : s.Version) + CacheRules;

    private async Task<CityPin?> PinAsync(double lat, double lng)
    {
        if (resolver == null) return null;
        try { return await resolver.ResolveAsync(lat, lng); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private async Task<TaxiWaysAnchor?> AnchorAsync(TaxiWaysNetworkSnapshot snapshot, string key, TaxiWaysOptions o, bool isEnd = false)
    {
        var pin = TaxiWaysKeys.TryParsePinned(key, out _, out var lat, out var lng) ? await PinAsync(lat, lng) : null;
        return TaxiWaysSearchEngine.AnchorFromKey(snapshot, key, o, isEnd, pin);
    }

    public async Task<WayPlanDetailDto?> PlanAsync(string planKey, int people, CancellationToken ct = default)
    {
        var o = options.Value;
        var snapshot = await network.GetAsync(ct) ?? throw new TaxiWaysUnavailableException();
        if (!TaxiWaysKeys.TryParsePlanKey(planKey, out var keys, o.MaxCars)) return null;

        var anchors = new List<TaxiWaysAnchor>();
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var anchor = await AnchorAsync(snapshot, key, o, i == keys.Count - 1);
            if (anchor == null) return null;
            anchors.Add(anchor);
        }

        var plan = TaxiWaysSearchEngine.BuildPlan(snapshot, anchors, Math.Clamp(people, 1, o.MaxPeople), o);
        if (plan == null) return null;

        var shown = plan.Legs.ToDictionary(l => l.Index, l => l.Drivers.Take(o.DetailDriversPerLeg).ToList());
        var contacts = await unitOfWork.TripEnquiries.LoadTaxiContactsAsync(
            shown.Values.SelectMany(d => d).Select(c => c.Driver.TaxiId).Distinct().ToList());

        var dto = new WayPlanDetailDto { PlanKey = plan.PlanKey, Cars = plan.Cars, Total = plan.Total };
        foreach (var leg in plan.Legs)
        {
            var drivers = shown[leg.Index].Select(c => WithContacts(TaxiWaysMapper.Driver(snapshot, c, leg.From, leg.To), contacts)).ToList();
            var legDto = TaxiWaysMapper.Leg(snapshot, leg, 0);
            legDto.Drivers = drivers;
            legDto.TopDrivers = drivers.Take(o.TopDriversPerLeg).ToList();
            legDto.Geometry = TaxiWaysMapper.Geometry(snapshot, leg.Top);
            dto.Legs.Add(legDto);
        }
        return dto;
    }

    public async Task<WayDriverPageDto?> LegDriversAsync(string legKey, LegDriverQuery q, CancellationToken ct = default)
    {
        var o = options.Value;
        var snapshot = await network.GetAsync(ct) ?? throw new TaxiWaysUnavailableException();
        if (!TaxiWaysKeys.TryParseLegKey(legKey, out var fromKey, out var toKey)) return null;
        var a = await AnchorAsync(snapshot, fromKey, o);
        var b = await AnchorAsync(snapshot, toKey, o, true);
        if (a == null || b == null) return null;

        var people = Math.Clamp(q.People, 1, o.MaxPeople);
        var all = TaxiWaysSearchEngine.Candidates(snapshot, a, b, people, o, basedHereFirst: q.ExcludeRoute == null, excludeRoute: q.ExcludeRoute);
        if (all.Count == 0) return null;

        IEnumerable<TaxiWaysCandidate> filtered = all;
        if (q.MinSeats is { } minSeats) filtered = filtered.Where(c => c.Driver.Seats >= minSeats);
        if (q.Category != null) filtered = filtered.Where(c => c.Driver.VehicleCategory == q.Category);
        if (q.RepliesInApp) filtered = filtered.Where(c => c.Driver.TripReplyLabel == TaxiTripReplyLabels.App);
        var list = filtered.ToList();
        if (q.Sort == TaxiWaysSorts.LowestPrice) list.Sort(TaxiWaysRanking.LowestPrice(people));

        var size = Math.Clamp(q.PageSize, 1, o.LegPageSizeMax);
        var page = list.Skip((Math.Max(1, q.Page) - 1) * size).Take(size).ToList();
        var contacts = await unitOfWork.TripEnquiries.LoadTaxiContactsAsync(page.Select(c => c.Driver.TaxiId).ToList());

        return new WayDriverPageDto
        {
            LegKey = legKey,
            Total = list.Count,
            HasMore = (Math.Max(1, q.Page) - 1) * size + page.Count < list.Count,
            Items = page.Select(c => WithContacts(TaxiWaysMapper.Driver(snapshot, c, TaxiWaysSearchEngine.StopOf(a), TaxiWaysSearchEngine.StopOf(b)), contacts)).ToList(),
        };
    }

    public async Task<Dictionary<Guid, LegTaxiFare>> LegFaresAsync(
        string legKey, IReadOnlyCollection<Guid> taxiIds, Guid? excludeRoute = null, CancellationToken ct = default)
    {
        var fares = new Dictionary<Guid, LegTaxiFare>();
        if (taxiIds.Count == 0 || !TaxiWaysKeys.TryParseLegKey(legKey, out var fromKey, out var toKey)) return fares;
        var o = options.Value;
        TaxiWaysNetworkSnapshot? snapshot;
        try
        {
            snapshot = await network.GetAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("taxi.ways.legfares network failed: {Error}", ex.Message);
            return fares;
        }
        if (snapshot == null) return fares;
        var a = await AnchorAsync(snapshot, fromKey, o);
        var b = await AnchorAsync(snapshot, toKey, o, true);
        if (a == null || b == null) return fares;

        var wanted = taxiIds.ToHashSet();
        foreach (var c in TaxiWaysSearchEngine.Candidates(snapshot, a, b, 1, o, basedHereFirst: excludeRoute == null, excludeRoute: excludeRoute))
            if (wanted.Contains(c.Driver.TaxiId))
                fares[c.Driver.TaxiId] = new LegTaxiFare(c.Driver.TaxiId, c.Link.RouteId, c.Driver.Seats, c.Link.EstimatedFare, c.Link.Partial);
        return fares;
    }

    private static WayDriverDto WithContacts(WayDriverDto d, Dictionary<Guid, (string? Phone, string? WhatsApp)> contacts)
    {
        if (contacts.TryGetValue(d.TaxiId, out var c))
        {
            d.Phone = c.Phone;
            d.WhatsApp = c.WhatsApp;
        }
        return d;
    }

    private async Task<TaxiWaysResultDto?> ComputeCoreAsync(
        TaxiWaysNetworkSnapshot snapshot, TaxiWaysAnchor from, TaxiWaysAnchor to, int people, TaxiWaysOptions o)
    {
        var search = TaxiWaysSearchEngine.Search(snapshot, from, to, people, o);
        if (search.Truncated)
            logger.LogWarning("taxi.ways.truncated {FromKey} {ToKey} {Expansions}", from.Key, to.Key, search.Expansions);

        var dto = new TaxiWaysResultDto
        {
            Version = snapshot.Version,
            AllTheWay = search.AllTheWay.Take(o.TopDrivers).Select(c => TaxiWaysMapper.Driver(snapshot, c, TaxiWaysSearchEngine.StopOf(from), TaxiWaysSearchEngine.StopOf(to))).ToList(),
            AllTheWayHasMore = search.AllTheWay.Count > o.TopDrivers,
            Plans = search.Plans.Select(p => TaxiWaysMapper.Plan(snapshot, p, o.TopDriversPerLeg)).ToList(),
            Truncated = search.Truncated,
        };
        dto.RoadLines = dto.AllTheWay.Any(d => d.HasRoadLine) || dto.Plans.Any(p => p.Legs.Any(l => l.HasRoadLine));

        if (dto.AllTheWay.Count > 0)
            dto.BestWay = new WayBestDto { Kind = TaxiWaysBestKinds.Driver, Id = dto.AllTheWay[0].TaxiId.ToString(), Reason = "1 car, all the way" };
        else if (dto.Plans.Count > 0)
        {
            var changes = dto.Plans[0].Cars - 1;
            dto.BestWay = new WayBestDto
            {
                Kind = TaxiWaysBestKinds.Plan,
                Id = dto.Plans[0].PlanKey,
                Reason = $"{changes} change{(changes == 1 ? "" : "s")}, about Rs {TaxiWaysMapper.Inr(dto.Plans[0].Total)}",
            };
        }
        else
        {
            var reach = TaxiWaysSearchEngine.NearestReachingPlace(snapshot, from, to, o);
            if (reach != null) dto.ReachFirstPlace = new WayPlaceDto { Key = reach.Key, Name = reach.Name, Lat = reach.Lat, Lng = reach.Lng };
        }
        return dto;
    }

    // The destination's local taxis and stays follow the same single-city rule as the routes: each belongs to the single nearest
    // Atlas city of its OWN coordinates and is shown only when that city is the destination's. Fetch = paged boxes of CityResolveMeters
    // around the destination (any district, up to CityListings.MaxCityFetch), cut by the city rule page by page; a taxi/stay
    // without coordinates is never connected.
    private async Task<IReadOnlyList<T>> InCityAsync<T>(
        TaxiWaysNetworkSnapshot snapshot, IReadOnlyList<T> rows, Func<T, (double Lat, double Lng)?> point, long? cityKey, TaxiWaysOptions o)
    {
        if (resolver == null) return TaxiWaysSearchEngine.InCity(snapshot, rows, point, cityKey, o);
        return cityKey == null ? [] : await resolver.FilterAsync(rows, point, cityKey.Value);
    }

    private async Task<DestBlock?> ComputeDestAsync(TaxiWaysNetworkSnapshot snapshot, TaxiWaysAnchor to, TaxiWaysOptions o)
    {
        // No city list (Atlas never answered): nothing is connected and nothing is cached, so the next search retries.
        if (snapshot.CityGrid.Count == 0) return null;
        using var scope = scopeFactory.CreateScope();
        var block = new DestBlock();
        var cityKey = to.Place?.CityKey;
        if (cityKey == null) return block;

        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var radiusKm = o.CityResolveMeters / 1000;
        var taxis = await CityListings.CollectAsync<(TaxiBrowseDto Item, double Lat, double Lng)>(
            async (take, skip) => await uow.Taxis.BrowseLocalNearAsync(to.Lat, to.Lng, radiusKm, localRateOnly: true, take, skip),
            rows => InCityAsync(snapshot, rows, x => (x.Lat, x.Lng), cityKey, o));
        block.LocalTaxis = taxis.Items.Select(x => x.Item).Take(o.MaxLocalTaxis).ToList();
        var stays = await CityListings.CollectAsync<StayNearbyDto>(
            async (take, skip) => (await uow.Stays.GetNearbyAsync(to.Lat, to.Lng, radiusKm, null, null, take, skip)).ToList(),
            rows => InCityAsync(snapshot, rows, st => ((double)st.Latitude, (double)st.Longitude), cityKey, o));
        block.Stays = stays.Items.Take(o.MaxStays).ToList();
        if (taxis.Capped || stays.Capped) logger.LogWarning("taxi.ways.dest.capped {CityKey} {Max}", cityKey, CityListings.MaxCityFetch);
        return block;
    }
}

// Typed names are per request and never live in the cached plan list: the cached copy is cloned before a name is set.
public static class TaxiWaysNameOverlay
{
    public static List<WayPlanDto> Apply(List<WayPlanDto> plans, string fromKey, string? fromName, string toKey, string? toName)
    {
        if (plans.Count == 0 || (fromName == null && toName == null)) return plans;
        var copy = JsonSerializer.Deserialize<List<WayPlanDto>>(JsonSerializer.Serialize(plans, TripJson.Options), TripJson.Options) ?? plans;
        foreach (var plan in copy) ApplyTo(plan, fromKey, fromName, toKey, toName);
        return copy;
    }

    public static void ApplyTo(WayPlanDto plan, string fromKey, string? fromName, string toKey, string? toName)
    {
        if (plan.Legs.Count == 0) return;
        var first = plan.Legs[0].From;
        var last = plan.Legs[^1].To;
        if (fromName != null && first.Key == fromKey && TaxiWaysKeys.IsVirtual(fromKey)) first.Name = fromName;
        if (toName != null && last.Key == toKey && TaxiWaysKeys.IsVirtual(toKey)) last.Name = toName;
    }
}

internal static class TaxiWaysMapper
{
    public static WayPlaceDto Place(TaxiWaysStop s) => new() { Key = s.Key, Name = s.Name, Lat = s.Lat, Lng = s.Lng };

    // The ridden end: the snapshot place of the link end, else the traveller's own point (a virtual pin) when it is that end.
    private static string RiddenName(TaxiWaysNetworkSnapshot snapshot, string placeKey, TaxiWaysStop? pickup, TaxiWaysStop? drop, string fallback)
    {
        if (snapshot.Places.GetValueOrDefault(placeKey)?.Name is { Length: > 0 } name) return name;
        if (pickup != null && pickup.Key == placeKey) return pickup.Name;
        if (drop != null && drop.Key == placeKey) return drop.Name;
        return fallback;
    }

    public static WayDriverDto Driver(TaxiWaysNetworkSnapshot snapshot, TaxiWaysCandidate c, TaxiWaysStop? pickup = null, TaxiWaysStop? drop = null)
    {
        var link = c.Link;
        var route = snapshot.Routes.GetValueOrDefault(link.RouteId);
        var lastRoute = link.Relay.Count > 0 ? snapshot.Routes.GetValueOrDefault(link.Relay[^1].RouteId) : route;
        var span = Math.Clamp(link.ToFraction - link.FromFraction, 0, 1);
        var d = c.Driver;
        var routeFrom = route?.FromName ?? string.Empty;
        var routeTo = lastRoute?.ToName ?? string.Empty;
        // A link is stored in the route's direction; a rider going the other way (round trip) starts at its far end.
        var startKey = c.Reversed ? link.ToPlace : link.FromPlace;
        var endKey = c.Reversed ? link.FromPlace : link.ToPlace;
        var estimated = link.Relay.Count > 0 ? link.Relay.Any(p => p.FareEstimated) : link.FareEstimated;
        return new WayDriverDto
        {
            TaxiId = d.TaxiId,
            DriverName = d.DriverName,
            VehicleTypeName = d.VehicleTypeName,
            VehicleCategory = d.VehicleCategory,
            Seats = d.Seats,
            PlateDisplay = d.PlateDisplay,
            CityName = d.CityName,
            BasedHere = c.BasedHere,
            TripReplyLabel = d.TripReplyLabel,
            PriorityRank = d.PriorityRank,
            IsFeatured = d.IsFeatured,
            RouteId = link.RouteId,
            Section = link.Partial || link.Relay.Count > 0 ? WayDriverSections.OnTheWay : WayDriverSections.Direct,
            FromName = RiddenName(snapshot, startKey, pickup, drop, c.Reversed ? routeTo : routeFrom),
            ToName = RiddenName(snapshot, endKey, pickup, drop, c.Reversed ? routeFrom : routeTo),
            RouteFromName = routeFrom,
            RouteToName = routeTo,
            FareEstimated = estimated,
            ListedFare = link.Fare,
            EstimatedFare = estimated ? link.EstimatedFare : null,
            TripType = link.TripType,
            Partial = link.Partial,
            Reversed = c.Reversed,
            DistanceKm = route?.DistanceKm is { } km ? Math.Round(km * span, 1) : null,
            Minutes = route?.Minutes is { } min ? (int)Math.Ceiling(min * span) : null,
            HasRoadLine = route?.HasLine == true,
            MeetAtName = link.MeetAtPlace is { } meet ? snapshot.Places.GetValueOrDefault(meet)?.Name : null,
        };
    }

    public static WayLegDto Leg(TaxiWaysNetworkSnapshot snapshot, TaxiWaysLegResult leg, int topDrivers)
    {
        var top = leg.Top;
        IEnumerable<string> via = top.Link.Via;
        if (top.Reversed) via = via.Reverse();
        var topDto = Driver(snapshot, top, leg.From, leg.To);
        return new WayLegDto
        {
            LegKey = leg.LegKey,
            Index = leg.Index,
            From = Place(leg.From),
            To = Place(leg.To),
            MeetingPlace = leg.MeetingPlace,
            ListedFare = leg.ListedFare,
            CarsNeeded = leg.CarsNeeded,
            SeatsNote = leg.SeatsNote,
            CoverTaxiIds = leg.CoverDrivers.Select(c => c.Driver.TaxiId).ToList(),
            FareEstimated = leg.FareEstimated,
            Partial = leg.Partial,
            DistanceKm = topDto.DistanceKm,
            Passes = via.Select(k => snapshot.Places.GetValueOrDefault(k)?.Name).OfType<string>().ToList(),
            PassStops = via.Where(k => k != leg.From.Key && k != leg.To.Key && snapshot.Places.ContainsKey(k))
                .Select(k => snapshot.Places[k])
                .Select(p => new WayPassStopDto { Key = p.Key, Name = p.Name, Lat = p.Lat, Lng = p.Lng }).ToList(),
            TopDrivers = leg.Drivers.Take(topDrivers).Select(c => Driver(snapshot, c, leg.From, leg.To)).ToList(),
            DriverCount = leg.Drivers.Count,
            HasRoadLine = topDto.HasRoadLine,
        };
    }

    public static WayPlanDto Plan(TaxiWaysNetworkSnapshot snapshot, TaxiWaysPlan plan, int topDrivers) => new()
    {
        PlanKey = plan.PlanKey,
        Cars = plan.Cars,
        Total = plan.Total,
        Legs = plan.Legs.Select(l => Leg(snapshot, l, topDrivers)).ToList(),
    };

    public static WayGeometryDto? Geometry(TaxiWaysNetworkSnapshot snapshot, TaxiWaysCandidate c)
    {
        if (!snapshot.Routes.TryGetValue(c.Link.RouteId, out var route) || !route.HasLine) return null;
        var points = TaxiWaysGeo.Slice(route.Line, route.Cumulative, c.Link.FromFraction, c.Link.ToFraction);
        if (c.Reversed) points.Reverse();
        return new WayGeometryDto
        {
            Coordinates = points.Select(p => new[] { Math.Round(p.Lng, 5), Math.Round(p.Lat, 5) }).ToList(),
        };
    }

    // Indian digit grouping (14,000 / 1,40,000) without depending on ICU culture data.
    public static string Inr(int amount)
    {
        var digits = Math.Abs((long)amount).ToString(CultureInfo.InvariantCulture);
        if (digits.Length > 3)
        {
            var head = digits[..^3];
            var tail = digits[^3..];
            var groups = new List<string>();
            while (head.Length > 2) { groups.Insert(0, head[^2..]); head = head[..^2]; }
            if (head.Length > 0) groups.Insert(0, head);
            digits = string.Join(',', groups) + "," + tail;
        }
        return amount < 0 ? "-" + digits : digits;
    }
}

// Version-keyed entries (no key scans): Redis with TTL, a short IMemoryCache entry for bursts, and an
// in-process single flight so identical concurrent searches compute once.
internal static class TaxiWaysPlanCache
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<object?>>> Inflight = new();

    public static async Task<(T? Value, string Source)> GetAsync<T>(
        IMemoryCache memory, IConnectionMultiplexer? redis, string key, TimeSpan redisTtl, TimeSpan memoryTtl,
        Func<Task<T?>> compute, ILogger logger, CancellationToken ct) where T : class
    {
        if (memory.TryGetValue(key, out T? cached) && cached != null) return (cached, "memory");

        var stored = await ReadAsync<T>(redis, key, logger);
        if (stored != null)
        {
            memory.Set(key, stored, memoryTtl);
            return (stored, "hit");
        }

        var lazy = Inflight.GetOrAdd(key, k => new Lazy<Task<object?>>(() => RunAsync(k, memory, redis, redisTtl, memoryTtl, compute, logger)));
        var value = await lazy.Value.WaitAsync(ct);
        return (value as T, "miss");
    }

    private static async Task<object?> RunAsync<T>(
        string key, IMemoryCache memory, IConnectionMultiplexer? redis, TimeSpan redisTtl, TimeSpan memoryTtl,
        Func<Task<T?>> compute, ILogger logger) where T : class
    {
        try
        {
            var value = await compute();
            if (value != null)
            {
                memory.Set(key, value, memoryTtl);
                await WriteAsync(redis, key, value, redisTtl, logger);
            }
            return value;
        }
        finally
        {
            Inflight.TryRemove(key, out _);
        }
    }

    private static async Task<T?> ReadAsync<T>(IConnectionMultiplexer? redis, string key, ILogger logger) where T : class
    {
        if (redis == null) return null;
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<T>(value.ToString(), TripJson.Options);
        }
        catch (Exception ex)
        {
            logger.LogWarning("taxi.ways.cache read {Key} failed: {Error}", key, ex.Message);
            return null;
        }
    }

    private static async Task WriteAsync<T>(IConnectionMultiplexer? redis, string key, T value, TimeSpan ttl, ILogger logger)
    {
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(value, TripJson.Options), ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning("taxi.ways.cache write {Key} failed: {Error}", key, ex.Message);
        }
    }
}
