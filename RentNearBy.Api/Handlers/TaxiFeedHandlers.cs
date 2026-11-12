using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public delegate Task<Dictionary<Guid, TaxiFeedItemDto>> TaxiFeedHydrate(IReadOnlyList<NearbyRow> rows);

public sealed record TaxiRouteFeedArgs(
    double FromLat, double FromLng, double ToLat, double ToLng, string? Category, int PageSize, string? Tier, string? PageToken);

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
        NearbyArgs args, string? category, INearbyFeedQuery query, INearbyContext context, TaxiFeedHydrate hydrate, CancellationToken ct = default)
    {
        var filter = new NearbyFilter(Category: category);
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
    public static async Task<List<TaxiFeedItemDto>> PassingAsync(
        Func<Task<TaxiWaysResultDto?>> ways, string? category, HashSet<Guid> skip, int max)
    {
        TaxiWaysResultDto? result;
        try { result = await ways(); }
        catch (TaxiWaysUnavailableException) { return []; }
        catch (HttpRequestException) { return []; }
        if (result == null) return [];

        var items = new List<TaxiFeedItemDto>();
        var seen = new HashSet<Guid>(skip);
        foreach (var d in result.AllTheWay)
        {
            if (items.Count >= max) break;
            if (category != null && d.VehicleCategory != category) continue;
            if (!seen.Add(d.TaxiId)) continue;
            items.Add(ForRoute(new TaxiFeedItemDto
            {
                Id = d.TaxiId, DriverName = d.DriverName, VehicleTypeName = d.VehicleTypeName, VehicleCategory = d.VehicleCategory,
                Seats = d.Seats, PlateDisplay = d.PlateDisplay, CityName = d.CityName, TripReplyLabel = d.TripReplyLabel,
                PriorityRank = d.PriorityRank, IsFeatured = d.IsFeatured, MatchedFare = d.ListedFare,
                MatchedFromName = d.FromName, MatchedToName = d.ToName, MatchedTripType = d.TripType, MatchReversed = d.Reversed,
            }));
        }
        return items;
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
        CancellationToken ct = default)
    {
        var size = a.PageSize;
        var tier = a.Tier;

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
            return new TaxiRouteFeedDto { Header = await context.HeaderAsync(a.FromLat, a.FromLng, null, ct), Tiers = [only] };
        }

        // Every drivers-on-route id (first 40) and every passing id is hidden from the distance tiers.
        var skip = new HashSet<Guid>();
        var first = tier == null ? await onRoute(1, size) : (Items: (IReadOnlyList<TaxiBrowseDto>)[], HasMore: false);
        var onRouteItems = first.Items.Select(x => ForRoute(TaxiFeedItemDto.From(x))).ToList();
        if (tier != null || first.HasMore)
            foreach (var x in (await onRoute(1, ExcludeFetch)).Items) skip.Add(x.Id);
        foreach (var i in onRouteItems) skip.Add(i.Id);

        var passing = await PassingAsync(ways, a.Category, skip, 20);
        foreach (var i in passing) skip.Add(i.Id);
        if (tier == TaxiTierKeys.Passing)
            return new TaxiRouteFeedDto
            {
                Header = await context.HeaderAsync(a.FromLat, a.FromLng, null, ct),
                Tiers = [new TaxiTierDto { Key = TaxiTierKeys.Passing, HeaderKey = "driversPassing", Items = passing }],
            };

        var after = NearbyToken.TryDecode(a.PageToken, out var token) ? token : null;
        var filter = new NearbyFilter(Category: a.Category, ExcludeIds: skip.ToList());
        var from = await DistanceTierAsync(
            TaxiTierKeys.BasedNearFrom, "basedNearFrom", a.FromLat, a.FromLng,
            tier == TaxiTierKeys.BasedNearFrom ? after : null, size, filter, query, context, hydrate, ct);
        if (tier == TaxiTierKeys.BasedNearFrom) return new TaxiRouteFeedDto { Header = from.Header!, Tiers = [from] };

        foreach (var i in from.Items) skip.Add(i.Id);
        filter = filter with { ExcludeIds = skip.ToList() };
        var to = await DistanceTierAsync(
            TaxiTierKeys.InTo, "taxisInTo", a.ToLat, a.ToLng,
            tier == TaxiTierKeys.InTo ? after : null, size, filter, query, context, hydrate, ct);
        if (tier == TaxiTierKeys.InTo) return new TaxiRouteFeedDto { Header = to.Header!, Tiers = [to] };

        var collapse = onRouteItems.Count + passing.Count >= 3;
        from.Collapsed = collapse;
        to.Collapsed = collapse;
        return new TaxiRouteFeedDto
        {
            Header = from.Header!,
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

    public static async Task<IResult> GetFeed(
        double? lat, double? lng, int? pageSize, string? pageToken, string? category,
        INearbyFeedQuery query, INearbyContext context, ApplicationDbContext db, IRateLimitService limiter,
        ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var limited = await RateLimitedAsync($"taxi:feed:{Caller(principal, http)}", FeedPerMinute, limiter, http);
        if (limited != null) return limited;
        if (!NearbyRules.ValidLatLng(lat, lng)) return BadRequestResponse("Invalid location");
        NearbyToken? after = null;
        if (pageToken != null && !NearbyToken.TryDecode(pageToken, out after)) return BadRequestResponse("Invalid page token");

        var size = Math.Clamp(pageSize ?? NearbyRules.DefaultPageSize, 1, NearbyRules.MaxPageSize);
        var args = new NearbyArgs(lat!.Value, lng!.Value, size, after);
        var page = await TaxiFeedComposer.LocalAsync(args, NormalizeCategory(category), query, context, rows => TaxiFeedHydrator.LoadAsync(db, rows), ct);
        return OkResponse(page);
    }

    public static async Task<IResult> GetRouteFeed(
        double? fromLat, double? fromLng, double? toLat, double? toLng, string? category, int? pageSize, string? tier, string? pageToken,
        INearbyFeedQuery query, INearbyContext context, IUnitOfWork uow, ITaxiWaysSearch waysSearch,
        ApplicationDbContext db, IRateLimitService limiter, IOptions<TaxiWaysOptions> options,
        ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var limited = await RateLimitedAsync($"taxi:feed:{userId}", FeedPerMinute, limiter, http);
        if (limited != null) return limited;
        if (!NearbyRules.ValidLatLng(fromLat, fromLng) || !NearbyRules.ValidLatLng(toLat, toLng)) return BadRequestResponse("Invalid location");
        if (tier != null && !Tiers.Contains(tier)) return BadRequestResponse("Invalid tier");
        if (tier is TaxiTierKeys.BasedNearFrom or TaxiTierKeys.InTo && pageToken != null && !NearbyToken.TryDecode(pageToken, out _))
            return BadRequestResponse("Invalid page token");

        // One ways-search check per request, the same rule as GetWays; the onRoute tier alone never calls the engine.
        if (tier != TaxiTierKeys.OnRoute)
        {
            limited = await RateLimitedAsync($"taxi:ways:search:{userId}", options.Value.SearchPerMinute, limiter, http);
            if (limited != null) return limited;
        }

        var size = Math.Clamp(pageSize ?? NearbyRules.TaxiTierPageSize, 1, 20);
        var cat = NormalizeCategory(category);
        var args = new TaxiRouteFeedArgs(fromLat!.Value, fromLng!.Value, toLat!.Value, toLng!.Value, cat, size, tier, pageToken);
        var dto = await TaxiFeedComposer.RouteAsync(
            args,
            (pageNo, take) => uow.Taxis.SearchByRoutePagedAsync(args.FromLat, args.FromLng, args.ToLat, args.ToLng, cat, pageNo, take),
            () => waysSearch.SearchAsync(
                new TaxiWaysQuery(args.FromLat, args.FromLng, args.ToLat, args.ToLng, options.Value.DefaultPeople, false, null, null), ct),
            query, context, rows => TaxiFeedHydrator.LoadAsync(db, rows), ct);
        return OkResponse(dto);
    }
}
