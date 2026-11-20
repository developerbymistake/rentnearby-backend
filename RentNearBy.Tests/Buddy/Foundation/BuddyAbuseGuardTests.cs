using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Handlers.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Buddy.Be1;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Tests.Buddy.Foundation;

public class BuddyAbuseGuardTests
{
    private static readonly BuddyRateLimit Limit = new("buddy:test:action", 2, TimeSpan.FromMinutes(1));

    [Theory]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(400, true)]
    [InlineData(409, true)]
    [InlineData(422, true)]
    public void Forged_or_foreign_calls_keep_the_permit_business_refusals_give_it_back(int status, bool givesBack)
        => Assert.Equal(givesBack, BuddyHttp.GivesPermitBack(ErrorResponse(status, "x", "X")));

    [Fact]
    public void A_success_never_gives_the_permit_back()
        => Assert.False(BuddyHttp.GivesPermitBack(OkResponse(new { })));

    [Fact]
    public void Error_types_follow_their_status()
    {
        Assert.False(BuddyHttp.GivesPermitBack(BuddyErrorTypes.TripNotFound));
        Assert.False(BuddyHttp.GivesPermitBack(BuddyErrorTypes.NotTripOwner));
        Assert.False(BuddyHttp.GivesPermitBack(BuddyErrorTypes.PersonNotFound));
        Assert.True(BuddyHttp.GivesPermitBack(BuddyErrorTypes.TryLater));
    }

    [Fact]
    public async Task Hammering_a_404_route_runs_into_the_limit()
    {
        var limiter = new InMemoryRateLimitService(() => 0);
        var http = new DefaultHttpContext();
        for (var i = 0; i < 2; i++)
            await BuddyHttp.ActionLimitAsync(limiter, http, Limit, () => Task.FromResult(ErrorResponse(404, "Trip not found", BuddyErrorTypes.TripNotFound)));
        var third = await BuddyHttp.ActionLimitAsync(limiter, http, Limit, () => Task.FromResult(OkResponse(new { })));
        Assert.Equal(429, Assert.IsAssignableFrom<IStatusCodeHttpResult>(third).StatusCode);
    }

    [Fact]
    public async Task A_conflict_gives_the_permit_back()
    {
        var limiter = new InMemoryRateLimitService(() => 0);
        var http = new DefaultHttpContext();
        for (var i = 0; i < 5; i++)
            await BuddyHttp.ActionLimitAsync(limiter, http, Limit, () => Task.FromResult(ErrorResponse(409, "x", BuddyErrorTypes.TryLater)));
        var next = await BuddyHttp.ActionLimitAsync(limiter, http, Limit, () => Task.FromResult(OkResponse(new { })));
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(next).StatusCode);
    }

    [Fact]
    public async Task Found_mark_is_per_finder_and_person()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var marks = new MemoryBuddyFoundMarks(cache);
        Guid me = Guid.NewGuid(), him = Guid.NewGuid();
        Assert.False(await marks.HasAsync(me, him));
        await marks.MarkAsync(me, him);
        Assert.True(await marks.HasAsync(me, him));
        Assert.False(await marks.HasAsync(him, me));
        Assert.Equal($"buddy:found:{me:N}:{him:N}", BuddyFoundMarkKeys.For(me, him));
    }
}
