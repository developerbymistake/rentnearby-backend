using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using TaxiAvailabilitySql = RentNearBy.Infrastructure.Services.TaxiAvailabilitySql;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public delegate Task<Dictionary<Guid, TaxiFeedItemDto>> TaxiFeedHydrate(IReadOnlyList<NearbyRow> rows);

// Day: the availability rule of that IST day (null = none, old callers); Cheapest: contract R12 sort.
public sealed record TaxiRouteFeedArgs(
    double FromLat, double FromLng, double ToLat, double ToLng, string? Category, int PageSize, string? Tier, string? PageToken,
    DateOnly? Day = null, bool Cheapest = false);

// The taxi ids of a candidate list that are NOT free on the day (one statement).
public delegate Task<IReadOnlySet<Guid>> TaxiBusyIds(IReadOnlyList<Guid> ids);

// freeCount: distinct free taxis of the whole onRoute set plus the given passing ids (one statement).
public delegate Task<int> TaxiFreeCount(IReadOnlyCollection<Guid> passingIds);

// Builds the taxi lists. Sources are passed in, so the layering rules are testable without a database.
public static class TaxiFeedComposer
{
    public const string RouteLabelKey = "onRoute";
    private const int ExcludeFetch = 40;

    private static string Token(NearbyRow row) => new NearbyToken(row.DistanceMeters, row.Id).Encode();

    private static void ApplyCentreCity(IEnumerable<TaxiFeedItemDto> items, NearbyHeaderDto header)
    {
        foreach (var i in items)
            if (i.DistanceKm is { } km) i.LabelKey = NearbyRules.LabelKey(km, i.CityName, header.CentreCityName);
    }

    private static List<TaxiFeedItemDto> InOrder(IEnumerable<NearbyRow> rows, Dictionary<Guid, TaxiFeedItemDto> byId)
    {
        var list = new List<TaxiFeedItemDto>();
        foreach (var r in rows)
            if (byId.TryGetValue(r.Id, out var item)) list.Add(item);
        return list;
    }

    public static async Task<NearbyPageDto<TaxiFeedItemDto>> LocalAsync(
        NearbyArgs args, string? category, INearbyFeedQuery query, INearbyContext context, TaxiFeedHydrate hydrate, CancellationToken ct = default,
        DateOnly? day = null)
    {
        var filter = new NearbyFilter(Category: category, Day: day);
        var rows = await query.PageAsync(NearbyKind.Taxi, args.Lat, args.Lng, args.After, args.PageSize + 1, filter, ct);
        var hasMore = rows.Count > args.PageSize;
        var page = rows.Take(args.PageSize).ToList();
        IReadOnlyList<NearbyRow> featuredRows = args.FirstPage
            ? await query.FeaturedAsync(NearbyKind.Taxi, args.Lat, args.Lng, NearbyRules.FeaturedMaxKm, NearbyRules.MaxFeatured, filter, ct)
            : [];

        var byId = await hydrate(page.Concat(featuredRows).ToList());
        var items = InOrder(page, byId);
        var featured = InOrder(featuredRows, byId);
        var header = await context.HeaderAsync(args.Lat, args.Lng, items.FirstOrDefault()?.DistanceKm, ct);
        ApplyCentreCity(items, header);
        ApplyCentreCity(featured, header);
        return new NearbyPageDto<TaxiFeedItemDto>
        {
            Header = header, Featured = featured, Items = items, HasMore = hasMore,
            NextToken = hasMore && page.Count > 0 ? Token(page[^1]) : null,
        };
    }

    private static TaxiFeedItemDto ForRoute(TaxiFeedItemDto i)
    {
        i.ListedForRoute = true;
        i.LabelKey = RouteLabelKey;
        i.IsNear = false;
        return i;
    }

    // Drivers whose listed route covers the whole way, from the ways engine. No engine or no network gives an empty list, never an error.
    // busy: drops the drivers not free on the day before the cap (the ways network is cached per version, so it holds no date).
    public static async Task<List<TaxiFeedItemDto>> PassingAsync(
        Func<Task<TaxiWaysResultDto?>> ways, string? category, HashSet<Guid> skip, int max, TaxiBusyIds? busy = null, bool cheapest = false)
    {
        TaxiWaysResultDto? result;
        try { result = await ways(); }
        catch (TaxiWaysUnavailableException) { return []; }
        catch (HttpRequestException) { return []; }
        if (result == null) return [];

        var items = new List<TaxiFeedItemDto>();
        var seen = new HashSet<Guid>(skip);
        var filtered = busy != null || cheapest;
        foreach (var d in result.AllTheWay)
        {
            if (!filtered && items.Count >= max) break;
            if (category != null && d.VehicleCategory != category) continue;
            if (!seen.Add(d.TaxiId)) continue;
            items.Add(ForRoute(new TaxiFeedItemDto
            {
                Id = d.TaxiId, DriverName = d.DriverName, VehicleTypeName = d.VehicleTypeName, VehicleCategory = d.VehicleCategory,
                Seats = d.Seats, PlateDisplay = d.PlateDisplay, DisplayPhotoUrl = d.DisplayPhotoUrl, CityName = d.CityName, TripReplyLabel = d.TripReplyLabel,
                PriorityRank = d.PriorityRank, IsFeatured = d.IsFeatured, MatchedFare = d.ListedFare,
                MatchedFromName = d.FromName, MatchedToName = d.ToName, MatchedTripType = d.TripType, MatchReversed = d.Reversed,
            }));
        }
        if (!filtered) return items;
        if (busy != null && items.Count > 0)
        {
            var off = await busy(items.Select(i => i.Id).ToList());
            if (off.Count > 0) items = items.Where(i => !off.Contains(i.Id)).ToList();
        }
        if (cheapest) items = items.OrderBy(i => i.MatchedFare is null ? 1 : 0).ThenBy(i => i.MatchedFare).ToList();
        return items.Take(max).ToList();
    }

    private static int ParseRoutePage(string? token)
    {
        if (token is { Length: > 1 } && token[0] == 'p'
            && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return Math.Clamp(n, 1, 1000);
        return 1;
    }

    private static async Task<TaxiTierDto> DistanceTierAsync(
        string key, string headerKey, double lat, double lng, NearbyToken? after, int size, NearbyFilter filter,
        INearbyFeedQuery query, INearbyContext context, TaxiFeedHydrate hydrate, CancellationToken ct)
    {
        var rows = await query.PageAsync(NearbyKind.Taxi, lat, lng, after, size + 1, filter, ct);
        var hasMore = rows.Count > size;
        var page = rows.Take(size).ToList();
        var items = InOrder(page, await hydrate(page));
        var header = await context.HeaderAsync(lat, lng, items.FirstOrDefault()?.DistanceKm, ct);
        ApplyCentreCity(items, header);
        return new TaxiTierDto
        {
            Key = key, HeaderKey = headerKey, Header = header, Items = items, HasMore = hasMore,
            NextToken = hasMore && page.Count > 0 ? Token(page[^1]) : null,
        };
    }

    // tier null: all four tiers, first page each. tier set: that tier only (the later pages of "show more").
    public static async Task<TaxiRouteFeedDto> RouteAsync(
        TaxiRouteFeedArgs a, Func<int, int, Task<(IReadOnlyList<TaxiBrowseDto> Items, bool HasMore)>> onRoute,
        Func<Task<TaxiWaysResultDto?>> ways, INearbyFeedQuery query, INearbyContext context, TaxiFeedHydrate hydrate,
        CancellationToken ct = default, TaxiBusyIds? busy = null, TaxiFreeCount? freeCount = null)
    {
        var size = a.PageSize;
        var tier = a.Tier;
        var date = a.Day is { } dd ? TaxiSearchDate.Text(dd) : null;

        if (tier == TaxiTierKeys.OnRoute)
        {
            var pageNo = ParseRoutePage(a.PageToken);
            var (found, more) = await onRoute(pageNo, size);
            var only = new TaxiTierDto
            {
                Key = TaxiTierKeys.OnRoute, HeaderKey = "driversOnRoute", HasMore = more,
                NextToken = more ? "p" + (pageNo + 1).ToString(CultureInfo.InvariantCulture) : null,
                Items = found.Select(x => ForRoute(TaxiFeedItemDto.From(x))).ToList(),
            };
            return new TaxiRouteFeedDto { Header = await context.HeaderAsync(a.FromLat, a.FromLng, null, ct), Tiers = [only], Date = date };
        }

        // Every drivers-on-route id (first 40) and every passing id is hidden from the distance tiers.
        // One ordered, offset-paged query: the first page of `size` is a prefix of the first page of ExcludeFetch.
        var skip = new HashSet<Guid>();
        (IReadOnlyList<TaxiBrowseDto> Items, bool HasMore) first = ([], false);
        if (tier == null)
        {
            var r = await onRoute(1, Math.Max(size, ExcludeFetch));
            first = (r.Items.Take(size).ToList(), r.Items.Count > size || r.HasMore);
            foreach (var x in r.Items) skip.Add(x.Id);
        }
        else
        {
            foreach (var x in (await onRoute(1, ExcludeFetch)).Items) skip.Add(x.Id);
        }
        var onRouteItems = first.Items.Select(x => ForRoute(TaxiFeedItemDto.From(x))).ToList();

        var passing = await PassingAsync(ways, a.Category, skip, 20, busy, a.Cheapest);
        foreach (var i in passing) skip.Add(i.Id);
        if (tier == TaxiTierKeys.Passing)
            return new TaxiRouteFeedDto
            {
                Header = await context.HeaderAsync(a.FromLat, a.FromLng, null, ct),
                Tiers = [new TaxiTierDto { Key = TaxiTierKeys.Passing, HeaderKey = "driversPassing", Items = passing }],
                Date = date,
            };

        var after = NearbyToken.TryDecode(a.PageToken, out var token) ? token : null;
        var filter = new NearbyFilter(Category: a.Category, ExcludeIds: skip.ToList(), Day: a.Day);
        var from = await DistanceTierAsync(
            TaxiTierKeys.BasedNearFrom, "basedNearFrom", a.FromLat, a.FromLng,
            tier == TaxiTierKeys.BasedNearFrom ? after : null, size, filter, query, context, hydrate, ct);
        if (tier == TaxiTierKeys.BasedNearFrom) return new TaxiRouteFeedDto { Header = from.Header!, Tiers = [from], Date = date };

        foreach (var i in from.Items) skip.Add(i.Id);
        filter = filter with { ExcludeIds = skip.ToList() };
        var to = await DistanceTierAsync(
            TaxiTierKeys.InTo, "taxisInTo", a.ToLat, a.ToLng,
            tier == TaxiTierKeys.InTo ? after : null, size, filter, query, context, hydrate, ct);
        if (tier == TaxiTierKeys.InTo) return new TaxiRouteFeedDto { Header = to.Header!, Tiers = [to], Date = date };

        var collapse = onRouteItems.Count + passing.Count >= 3;
        from.Collapsed = collapse;
        to.Collapsed = collapse;
        int? free = freeCount != null ? await freeCount(passing.Select(p => p.Id).ToList()) : null;
        return new TaxiRouteFeedDto
        {
            Header = from.Header!,
            Date = date,
            FreeCount = free,
            Tiers =
            [
                new TaxiTierDto
                {
                    Key = TaxiTierKeys.OnRoute, HeaderKey = "driversOnRoute", Items = onRouteItems, HasMore = first.HasMore,
                    NextToken = first.HasMore ? "p2" : null,
                },
                new TaxiTierDto { Key = TaxiTierKeys.Passing, HeaderKey = "driversPassing", Items = passing },
                from,
                to,
            ],
        };
    }
}

// Public taxi lists ordered by distance. Never empty while any driver exists.
public static class TaxiFeedHandlers
{
    private static readonly TimeSpan FeedWindow = TimeSpan.FromMinutes(1);
    private const int FeedPerMinute = 120;
    private static readonly string[] Tiers =
        [TaxiTierKeys.OnRoute, TaxiTierKeys.Passing, TaxiTierKeys.BasedNearFrom, TaxiTierKeys.InTo];

    private static string? NormalizeCategory(string? category) =>
        category != null && TaxiVehicleCategories.All.Contains(category) ? category : null;

    private static async Task<IResult?> RateLimitedAsync(string key, int max, IRateLimitService limiter, HttpContext http)
    {
        var rl = await limiter.CheckAsync(key, max, FeedWindow);
        if (rl.IsAllowed) return null;
        http.Response.Headers["Retry-After"] = ((int)(rl.RetryAfter ?? FeedWindow).TotalSeconds).ToString(CultureInfo.InvariantCulture);
        return TooManyRequestsResponse();
    }

    private static string Caller(ClaimsPrincipal principal, HttpContext http) =>
        UsersHandlers.TryGetUserId(principal, out var id) ? id.ToString() : http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public const string SortNearest = "nearest";
    public const string SortCheapest = "cheapest";

    public static DateOnly TodayIst(TimeProvider? clock) => IstTime.TodayIst((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);

    public static IResult InvalidDate() => ErrorResponse(400, TaxiSearchDate.InvalidDateMessage, TaxiSearchDate.InvalidDateType);

    public static async Task<IResult> GetFeed(
        double? lat, double? lng, int? pageSize, string? pageToken, string? category,
        INearbyFeedQuery query, INearbyContext context, ApplicationDbContext db, IRateLimitService limiter,
        ClaimsPrincipal principal, HttpContext http, CancellationToken ct, string? date = null, [FromServices] TimeProvider? clock = null)
    {
        var limited = await RateLimitedAsync($"taxi:feed:{Caller(principal, http)}", FeedPerMinute, limiter, http);
        if (limited != null) return limited;
        if (!NearbyRules.ValidLatLng(lat, lng)) return BadRequestResponse("Invalid location");
        NearbyToken? after = null;
        if (pageToken != null && !NearbyToken.TryDecode(pageToken, out after)) return BadRequestResponse("Invalid page token");

        var day = TaxiSearchDate.Resolve(date, TodayIst(clock));
        if (!day.Ok) return InvalidDate();

        var size = Math.Clamp(pageSize ?? NearbyRules.DefaultPageSize, 1, NearbyRules.MaxPageSize);
        var args = new NearbyArgs(lat!.Value, lng!.Value, size, after);
        var page = await TaxiFeedComposer.LocalAsync(args, NormalizeCategory(category), query, context, rows => TaxiFeedHydrator.LoadAsync(db, rows), ct,
            day.Day);
        return OkResponse(page);
    }

    public static async Task<IResult> GetRouteFeed(
        double? fromLat, double? fromLng, double? toLat, double? toLng, string? category, int? pageSize, string? tier, string? pageToken,
        INearbyFeedQuery query, INearbyContext context, IUnitOfWork uow, ITaxiWaysSearch waysSearch,
        ApplicationDbContext db, IRateLimitService limiter, IOptions<TaxiWaysOptions> options,
        ClaimsPrincipal principal, HttpContext http, CancellationToken ct, string? date = null, string? sort = null,
        long? fromCityKey = null, long? toCityKey = null, [FromServices] TimeProvider? clock = null)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var limited = await RateLimitedAsync($"taxi:feed:{userId}", FeedPerMinute, limiter, http);
        if (limited != null) return limited;
        if (!NearbyRules.ValidLatLng(fromLat, fromLng) || !NearbyRules.ValidLatLng(toLat, toLng)) return BadRequestResponse("Invalid location");
        if (tier != null && !Tiers.Contains(tier)) return BadRequestResponse("Invalid tier");
        if (tier is TaxiTierKeys.BasedNearFrom or TaxiTierKeys.InTo && pageToken != null && !NearbyToken.TryDecode(pageToken, out _))
            return BadRequestResponse("Invalid page token");
        if (sort != null && sort != SortNearest && sort != SortCheapest) return BadRequestResponse("Invalid sort");
        var cheapest = sort == SortCheapest;
        if (!TaxiRouteCityPair.TryCreate(fromCityKey, toCityKey, out var cities)) return BadRequestResponse("Invalid city keys");
        var day = TaxiSearchDate.Resolve(date, TodayIst(clock));
        if (!day.Ok) return InvalidDate();

        // One ways-search check per request, the same rule as GetWays; the onRoute tier alone never calls the engine.
        if (tier != TaxiTierKeys.OnRoute)
        {
            limited = await RateLimitedAsync($"taxi:ways:search:{userId}", options.Value.SearchPerMinute, limiter, http);
            if (limited != null) return limited;
        }

        var size = Math.Clamp(pageSize ?? NearbyRules.TaxiTierPageSize, 1, 20);
        var cat = NormalizeCategory(category);
        var args = new TaxiRouteFeedArgs(fromLat!.Value, fromLng!.Value, toLat!.Value, toLng!.Value, cat, size, tier, pageToken, day.Day, cheapest);
        TaxiFreeCount? freeCount = tier == null
            ? passingIds => uow.Taxis.CountRouteFreeAsync(args.FromLat, args.FromLng, args.ToLat, args.ToLng, cat, day.Day, passingIds, cities)
            : null;
        var dto = await TaxiFeedComposer.RouteAsync(
            args,
            (pageNo, take) => uow.Taxis.SearchByRoutePagedAsync(args.FromLat, args.FromLng, args.ToLat, args.ToLng, cat, pageNo, take, day.Day, cheapest, cities),
            () => waysSearch.SearchAsync(
                new TaxiWaysQuery(args.FromLat, args.FromLng, args.ToLat, args.ToLng, options.Value.DefaultPeople, false, null, null), ct),
            query, context, rows => TaxiFeedHydrator.LoadAsync(db, rows), ct,
            ids => BusyAsync(db, ids, day.Day, ct), freeCount);
        return OkResponse(dto);
    }

    // Passing drivers not free on the day (the availability rule, one statement on the given ids).
    public static readonly string BusySql =
        $"""SELECT t."Id" AS "Value" FROM "TaxiListings" t WHERE t."Id" = ANY(@ids) AND NOT ({TaxiAvailabilitySql.PublicOn("t", TaxiAvailabilitySql.DayParameter)})""";

    public static async Task<IReadOnlySet<Guid>> BusyAsync(ApplicationDbContext db, IReadOnlyList<Guid> ids, DateOnly day, CancellationToken ct)
    {
        if (ids.Count == 0) return new HashSet<Guid>();
        var rows = await db.Database.SqlQueryRaw<Guid>(BusySql,
            new Npgsql.NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ids.Distinct().ToArray() },
            TaxiAvailabilitySql.Day(day)).ToListAsync(ct);
        return rows.ToHashSet();
    }
}
