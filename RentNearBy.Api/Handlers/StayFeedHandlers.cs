using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class StayFeedHandlers
{
    public static async Task<IResult> GetFeed(double? lat, double? lng, int? pageSize, string? pageToken, Guid? stayTypeId, string? meals,
        INearbyFeedQuery query, INearbyContext ctx, ApplicationDbContext db, CancellationToken ct)
    {
        var bad = NearbyFeedShared.TryParse(lat, lng, pageSize, pageToken, out var args);
        if (bad != null) return bad;
        var filter = new NearbyFilter(StayTypeId: stayTypeId, Meals: meals != null && StayMeals.All.Contains(meals) ? meals : null);
        return OkResponse(await BuildAsync(args, filter, query, ctx, rows => StayFeedHydrator.LoadAsync(db, rows), ct));
    }

    // The hydrator is a parameter so the whole page logic is testable without a database.
    public static async Task<NearbyPageDto<StayFeedItemDto>> BuildAsync(NearbyArgs args, NearbyFilter filter,
        INearbyFeedQuery query, INearbyContext ctx,
        Func<IReadOnlyList<NearbyRow>, Task<Dictionary<Guid, StayFeedItemDto>>> load, CancellationToken ct)
    {
        var rowsPlus = await query.PageAsync(NearbyKind.Stay, args.Lat, args.Lng, args.After, args.PageSize + 1, filter, ct);
        var (page, hasMore) = NearbyFeedShared.Split(rowsPlus, args.PageSize);

        IReadOnlyList<NearbyRow> featuredRows = [];
        if (args.FirstPage)
        {
            var raw = await query.FeaturedAsync(NearbyKind.Stay, args.Lat, args.Lng, NearbyRules.FeaturedMaxKm, NearbyRules.MaxFeatured, filter, ct);
            featuredRows = raw.Where(r => r.DistanceMeters <= NearbyRules.FeaturedMaxKm * 1000.0)
                .Take(NearbyRules.MaxFeatured).ToList();
        }

        var loaded = await load(page.Concat(featuredRows).DistinctBy(r => r.Id).ToList());
        var items = Pick(page, loaded);
        var featured = Pick(featuredRows, loaded);

        var first = page.Count > 0 ? page[0] : featuredRows.Count > 0 ? featuredRows[0] : null;
        double? nearestKm = first == null ? null : NearbyRules.StayKm(first.DistanceMeters);
        var header = await ctx.HeaderAsync(args.Lat, args.Lng, nearestKm, ct);

        foreach (var item in items.Concat(featured))
            item.LabelKey = NearbyRules.LabelKey(item.DistanceKm, item.CityName, header.CentreCityName);

        return NearbyFeedShared.Compose(header, featured, items, page.Count > 0 ? page[^1] : null, hasMore);
    }

    private static List<StayFeedItemDto> Pick(IReadOnlyList<NearbyRow> rows, Dictionary<Guid, StayFeedItemDto> loaded)
    {
        var list = new List<StayFeedItemDto>(rows.Count);
        foreach (var r in rows)
            if (loaded.TryGetValue(r.Id, out var item))
                list.Add(item);
        return list;
    }
}
