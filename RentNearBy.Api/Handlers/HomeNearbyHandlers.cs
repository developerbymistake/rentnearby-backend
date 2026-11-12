using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class HomeNearbyHandlers
{
    private const int DefaultLimit = 5;
    private const int MaxLimit = 10;
    private const int DefaultRecentLimit = 10;
    private const int MaxRecentLimit = 20;

    // The three data sources of the Home rails, injectable so the flow runs without a database.
    public sealed record Loaders(
        Func<IReadOnlyList<NearbyRow>, Task<Dictionary<Guid, StayFeedItemDto>>> Stays,
        Func<IReadOnlyList<NearbyRow>, Task<Dictionary<Guid, TaxiFeedItemDto>>> Taxis,
        Func<int, Task<List<StayBrowseDto>>> Recent);

    public static Task<IResult> GetNearbyFeed(
        double? lat, double? lng, int? limit, int? recentLimit, string? sections,
        IUnitOfWork unitOfWork, IMemoryCache cache, INearbyFeedQuery query, INearbyContext ctx,
        ApplicationDbContext db, CancellationToken ct = default)
        => BuildAsync(lat, lng, limit, recentLimit, sections, unitOfWork, cache, query, ctx,
            new Loaders(
                rows => StayFeedHydrator.LoadAsync(db, rows),
                rows => TaxiFeedHydrator.LoadAsync(db, rows),
                take => unitOfWork.Stays.GetRecentProjectedAsync(take)),
            ct);

    // Public so tests can drive the whole flow with fakes.
    public static async Task<IResult> BuildAsync(
        double? lat, double? lng, int? limit, int? recentLimit, string? sections,
        IUnitOfWork unitOfWork, IMemoryCache cache, INearbyFeedQuery query, INearbyContext ctx,
        Loaders loaders, CancellationToken ct = default)
    {
        if (!NearbyRules.ValidLatLng(lat, lng)) return BadRequestResponse("Invalid location");
        var (la, lo) = (lat!.Value, lng!.Value);

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var recentTake = Math.Clamp(recentLimit ?? DefaultRecentLimit, 1, MaxRecentLimit);

        var tabs = await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache);
        var feed = new HomeNearbyFeedDto
        {
            Tabs = new HomeFeedTabsDto
            {
                Stays = TabState.IsActive(tabs, AppTabKeys.Stays),
                Jobs = TabState.IsActive(tabs, AppTabKeys.Jobs),
                Taxi = TabState.IsActive(tabs, AppTabKeys.Taxi),
            },
        };

        HashSet<string>? requested = string.IsNullOrWhiteSpace(sections)
            ? null
            : sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Want(string section, bool tabOn) => tabOn && (requested == null || requested.Contains(section));

        double? nearestKm = null;
        var filter = new NearbyFilter();

        if (Want(HomeHandlers.FeedSections.Stays, feed.Tabs.Stays))
        {
            var rows = await query.PageAsync(NearbyKind.Stay, la, lo, null, take, filter, ct);
            var loaded = await loaders.Stays(rows);
            feed.Stays = rows.Where(r => loaded.ContainsKey(r.Id)).Select(r => loaded[r.Id]).ToList();
            if (rows.Count > 0) nearestKm = NearbyRules.StayKm(rows[0].DistanceMeters);
            feed.Header = await ctx.HeaderAsync(la, lo, nearestKm, ct);
        }

        if (Want(HomeHandlers.FeedSections.Taxis, feed.Tabs.Taxi))
        {
            var rows = await query.PageAsync(NearbyKind.Taxi, la, lo, null, take, filter, ct);
            var loaded = await loaders.Taxis(rows);
            feed.Taxis = rows.Where(r => loaded.ContainsKey(r.Id)).Select(r => loaded[r.Id]).ToList();
            if (feed.Header == null)
                feed.Header = await ctx.HeaderAsync(la, lo, rows.Count > 0 ? NearbyRules.TaxiKm(rows[0].DistanceMeters) : null, ct);
        }

        if (Want(HomeHandlers.FeedSections.RecentStays, feed.Tabs.Stays))
            feed.RecentStays = (await loaders.Recent(StayCache.RecentMax)).Take(recentTake).ToList();

        return OkResponse(feed);
    }
}
