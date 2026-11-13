using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;
using RentNearBy.Tests.Nearby;

namespace RentNearBy.Tests.Taxis;

// The first route-feed page asks the on-route source once; items, HasMore, NextToken and the exclusion set stay as before.
public class TaxiRouteFeedSingleCallTests
{
    private sealed class OnRouteSource(int total)
    {
        public readonly List<TaxiBrowseDto> All = Enumerable.Range(0, total).Select(_ => new TaxiBrowseDto { Id = Guid.NewGuid() }).ToList();
        public readonly List<(int Page, int Take)> Calls = [];

        // Offset paging over one fixed order, like SearchByRoutePagedAsync.
        public Task<(IReadOnlyList<TaxiBrowseDto> Items, bool HasMore)> Get(int page, int take)
        {
            Calls.Add((page, take));
            var rows = All.Skip((page - 1) * take).Take(take + 1).ToList();
            return Task.FromResult<(IReadOnlyList<TaxiBrowseDto>, bool)>((rows.Take(take).ToList(), rows.Count > take));
        }
    }

    private static async Task<(TaxiRouteFeedDto Dto, OnRouteSource Src, FakeNearbyQuery Query)> Run(int total, int size, string? tier = null)
    {
        var src = new OnRouteSource(total);
        var query = new FakeNearbyQuery([]);
        var dto = await TaxiFeedComposer.RouteAsync(
            new TaxiRouteFeedArgs(29.2, 79.5, 29.4, 79.4, null, size, tier, null),
            src.Get, () => Task.FromResult<TaxiWaysResultDto?>(null),
            query, new FakeNearbyContext(), _ => Task.FromResult(new Dictionary<Guid, TaxiFeedItemDto>()));
        return (dto, src, query);
    }

    // What the old two-call code produced: page 1 of `size`, plus the first 40 ids only when there was more.
    private static (List<Guid> Items, bool HasMore, List<Guid> Excluded) Expected(OnRouteSource src, int size)
    {
        var items = src.All.Take(size).Select(x => x.Id).ToList();
        var hasMore = src.All.Count > size;
        var excluded = new List<Guid>();
        if (hasMore) excluded.AddRange(src.All.Take(40).Select(x => x.Id));
        foreach (var id in items) if (!excluded.Contains(id)) excluded.Add(id);
        return (items, hasMore, excluded);
    }

    [Theory]
    [InlineData(3, 5)]
    [InlineData(0, 5)]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(45, 5)]
    [InlineData(40, 20)]
    [InlineData(41, 20)]
    [InlineData(100, 1)]
    [InlineData(20, 20)]
    [InlineData(21, 20)]
    public async Task First_page_matches_the_old_result_with_one_on_route_call(int total, int size)
    {
        var (dto, src, query) = await Run(total, size);
        var expected = Expected(src, size);

        var onRoute = dto.Tiers.Single(t => t.Key == TaxiTierKeys.OnRoute);
        Assert.Equal(expected.Items, onRoute.Items.Select(i => i.Id));
        Assert.All(onRoute.Items, i => Assert.True(i.ListedForRoute));
        Assert.Equal(expected.HasMore, onRoute.HasMore);
        Assert.Equal(expected.HasMore ? "p2" : null, onRoute.NextToken);

        Assert.Single(src.Calls);
        Assert.Equal((1, 40), src.Calls[0]);

        Assert.Equal(2, query.Calls.Count);
        Assert.Equal(expected.Excluded, query.Calls[0].Filter.ExcludeIds);
        Assert.Equal(expected.Excluded, query.Calls[1].Filter.ExcludeIds);
    }

    [Theory]
    [InlineData(TaxiTierKeys.Passing)]
    [InlineData(TaxiTierKeys.BasedNearFrom)]
    [InlineData(TaxiTierKeys.InTo)]
    public async Task A_later_tier_page_still_excludes_the_first_forty_route_drivers(string tier)
    {
        var (dto, src, query) = await Run(45, 5, tier);
        Assert.Equal([(1, 40)], src.Calls);
        Assert.Single(dto.Tiers);
        if (tier != TaxiTierKeys.Passing)
            Assert.Equal(src.All.Take(40).Select(x => x.Id), query.Calls[0].Filter.ExcludeIds);
    }
}
