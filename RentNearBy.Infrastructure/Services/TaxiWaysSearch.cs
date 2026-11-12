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
    ILogger<TaxiWaysSearch> logger) : ITaxiWaysSearch
{
    internal sealed class DestBlock
    {
        public Guid? DistrictId { get; set; }
        public List<TaxiBrowseDto> LocalTaxis { get; set; } = new();
        public List<StayNearbyDto> Stays { get; set; } = new();
    }

    public async Task<TaxiWaysResultDto?> SearchAsync(TaxiWaysQuery q, CancellationToken ct = default)
    {
        var o = options.Value;
        var watch = Stopwatch.StartNew();
        var snapshot = await network.GetAsync(ct);
        if (snapshot == null) return null;

        var from = TaxiWaysSearchEngine.Resolve(snapshot, q.FromLat, q.FromLng, q.FromMyLocation, o);
        var to = TaxiWaysSearchEngine.Resolve(snapshot, q.ToLat, q.ToLng, false, o, TaxiWaysLabels.Drop);
        var people = Math.Clamp(q.People, 1, o.MaxPeople);
        var redis = sp.GetService<IConnectionMultiplexer>();
        var memoryTtl = TimeSpan.FromSeconds(o.MemoryCacheSeconds);

        var (core, source) = await TaxiWaysPlanCache.GetAsync(
            memory, redis, TaxiWaysKeys.Plan(snapshot.Version, from.Key, to.Key, people),
            TimeSpan.FromMinutes(o.PlanCacheMinutes), memoryTtl,
            () => ComputeCoreAsync(snapshot, from, to, people, o), logger, ct);

        var (dest, _) = await TaxiWaysPlanCache.GetAsync(
            memory, redis, TaxiWaysKeys.Dest(snapshot.Version, to.Key),
            TimeSpan.FromMinutes(o.DestCacheMinutes), memoryTtl,
            () => ComputeDestAsync(to, o), logger, ct);

        if (core == null) return null;

        var fromName = from.Resolved ? from.Name : TaxiWaysLabels.Of(q.FromName, from.Key, true, false);
        var toName = to.Resolved ? to.Name : TaxiWaysLabels.Of(q.ToName, to.Key, false, true);
        var result = new TaxiWaysResultDto
        {
            Version = core.Version,
            From = new WayPlaceDto { Key = from.Key, Name = fromName, Lat = from.Lat, Lng = from.Lng, Resolved = from.Resolved },
            To = new WayPlaceDto { Key = to.Key, Name = toName, Lat = to.Lat, Lng = to.Lng, Resolved = to.Resolved, DistrictId = dest?.DistrictId },
            BestWay = core.BestWay,
            AllTheWay = core.AllTheWay,
            AllTheWayHasMore = core.AllTheWayHasMore,
            Plans = TaxiWaysNameOverlay.Apply(core.Plans, from.Key, from.Resolved ? null : fromName, to.Key, to.Resolved ? null : toName),
            Truncated = core.Truncated,
            LocalTaxis = dest?.LocalTaxis ?? new(),
            Stays = dest?.Stays ?? new(),
            ReachFirstPlace = core.ReachFirstPlace,
            RoadLines = core.RoadLines,
        };

        logger.LogInformation("taxi.ways.search {FromKey} {ToKey} {Cars} {Plans} {Ms} {Cache}",
            from.Key, to.Key, result.Plans.Count == 0 ? (result.AllTheWay.Count > 0 ? 1 : 0) : result.Plans[0].Cars,
            result.Plans.Count, watch.ElapsedMilliseconds, source);
        return result;
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
            var anchor = TaxiWaysSearchEngine.AnchorFromKey(snapshot, key, o, i == keys.Count - 1);
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
            var drivers = shown[leg.Index].Select(c => WithContacts(TaxiWaysMapper.Driver(snapshot, c), contacts)).ToList();
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
        var a = TaxiWaysSearchEngine.AnchorFromKey(snapshot, fromKey, o);
        var b = TaxiWaysSearchEngine.AnchorFromKey(snapshot, toKey, o, true);
        if (a == null || b == null) return null;

        var people = Math.Clamp(q.People, 1, o.MaxPeople);
        var area = q.Area ?? q.ExcludeRoute == null;
        var endArea = q.EndArea ?? (area || !b.Resolved);
        var all = TaxiWaysSearchEngine.Candidates(snapshot, a, b, people, o, basedHereFirst: area, excludeRoute: q.ExcludeRoute, area: area, endArea: endArea);
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
            Items = page.Select(c => WithContacts(TaxiWaysMapper.Driver(snapshot, c), contacts)).ToList(),
        };
    }

    public async Task<Dictionary<Guid, LegTaxiFare>> LegFaresAsync(
        string legKey, IReadOnlyCollection<Guid> taxiIds, Guid? excludeRoute = null, CancellationToken ct = default,
        bool? area = null, bool? endArea = null)
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
        var a = TaxiWaysSearchEngine.AnchorFromKey(snapshot, fromKey, o);
        var b = TaxiWaysSearchEngine.AnchorFromKey(snapshot, toKey, o, true);
        if (a == null || b == null) return fares;

        var wanted = taxiIds.ToHashSet();
        var isFirst = area ?? excludeRoute == null;
        var reachesEnd = endArea ?? (isFirst || !b.Resolved);
        foreach (var c in TaxiWaysSearchEngine.Candidates(snapshot, a, b, 1, o, basedHereFirst: isFirst, excludeRoute: excludeRoute, area: isFirst, endArea: reachesEnd))
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
            AllTheWay = search.AllTheWay.Take(o.TopDrivers).Select(c => TaxiWaysMapper.Driver(snapshot, c)).ToList(),
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

    private async Task<DestBlock?> ComputeDestAsync(TaxiWaysAnchor to, TaxiWaysOptions o)
    {
        using var scope = scopeFactory.CreateScope();
        var districtId = await DistrictOfAsync(scope.ServiceProvider, to.Lat, to.Lng, o);
        var block = new DestBlock { DistrictId = districtId };
        if (districtId == null) return block;

        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var (taxis, _) = await uow.Taxis.BrowseLocalPagedAsync(districtId.Value, null, null, 1, o.MaxLocalTaxis, cityOnly: false, localRateOnly: true);
        block.LocalTaxis = taxis.ToList();
        var stays = await uow.Stays.GetNearbyAsync(to.Lat, to.Lng, o.BasedHereMeters / 1000, districtId.Value, null);
        block.Stays = stays.Take(o.MaxStays).ToList();
        return block;
    }

    private const string NoDistrict = "none";

    private async Task<Guid?> DistrictOfAsync(IServiceProvider scoped, double lat, double lng, TaxiWaysOptions o)
    {
        var key = TaxiWaysKeys.District(lat, lng);
        if (memory.TryGetValue(key, out string? known) && known != null)
            return Guid.TryParse(known, out var g) ? g : null;

        var redis = sp.GetService<IConnectionMultiplexer>();
        if (redis != null)
        {
            try
            {
                var value = await redis.GetDatabase().StringGetAsync(key);
                if (value.HasValue)
                {
                    memory.Set(key, value.ToString(), TimeSpan.FromHours(1));
                    return Guid.TryParse(value.ToString(), out var g) ? g : null;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("taxi.ways.district read failed: {Error}", ex.Message);
            }
        }

        var (districtId, _) = await scoped.GetRequiredService<ITaxiWaysNetworkSource>().FindDistrictAsync(lat, lng);
        var text = districtId?.ToString() ?? NoDistrict;
        memory.Set(key, text, TimeSpan.FromHours(1));
        if (redis != null)
        {
            try { await redis.GetDatabase().StringSetAsync(key, text, TimeSpan.FromDays(o.DistrictCacheDays)); }
            catch (Exception ex) { logger.LogWarning("taxi.ways.district write failed: {Error}", ex.Message); }
        }
        if (districtId == null) logger.LogInformation("taxi.ways.district none {Lat} {Lng}", lat, lng);
        return districtId;
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

    public static WayDriverDto Driver(TaxiWaysNetworkSnapshot snapshot, TaxiWaysCandidate c)
    {
        var route = snapshot.Routes.GetValueOrDefault(c.Link.RouteId);
        var span = Math.Clamp(c.Link.ToFraction - c.Link.FromFraction, 0, 1);
        var d = c.Driver;
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
            RouteId = c.Link.RouteId,
            FromName = route == null ? string.Empty : c.Reversed ? route.ToName : route.FromName,
            ToName = route == null ? string.Empty : c.Reversed ? route.FromName : route.ToName,
            ListedFare = c.Link.Fare,
            EstimatedFare = c.Link.FareEstimated ? c.Link.EstimatedFare : null,
            TripType = c.Link.TripType,
            Partial = c.Link.Partial,
            Reversed = c.Reversed,
            DistanceKm = route?.DistanceKm is { } km ? Math.Round(km * span, 1) : null,
            Minutes = route?.Minutes is { } min ? (int)Math.Ceiling(min * span) : null,
            HasRoadLine = route?.HasLine == true,
            MeetAtName = c.Link.MeetAtPlace is { } meet ? snapshot.Places.GetValueOrDefault(meet)?.Name : null,
        };
    }

    public static WayLegDto Leg(TaxiWaysNetworkSnapshot snapshot, TaxiWaysLegResult leg, int topDrivers)
    {
        var top = leg.Top;
        IEnumerable<string> via = top.Link.Via;
        if (top.Reversed) via = via.Reverse();
        var topDto = Driver(snapshot, top);
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
            TopDrivers = leg.Drivers.Take(topDrivers).Select(c => Driver(snapshot, c)).ToList(),
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
