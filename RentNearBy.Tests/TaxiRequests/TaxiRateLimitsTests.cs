using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Tests.TaxiRequests;

// O16: status poll and list reads 60 a minute, actions 30 a minute, per user; 429 + Retry-After; a refused action gives
// its permit back.
public class TaxiRateLimitsTests
{
    private static ClaimsPrincipal User(Guid id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test"));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 200;

    private static async Task SpendAsync(IRateLimitService limiter, string key, int max, int count)
    {
        for (var i = 0; i < count; i++) Assert.True((await limiter.CheckAsync(key, max, TaxiRateLimits.Window)).IsAllowed);
    }

    private static void AssertLimited(IResult result, HttpContext http)
    {
        Assert.Equal(429, StatusOf(result));
        Assert.True(int.Parse(http.Response.Headers["Retry-After"].ToString()) > 0);
    }

    // Each case runs the route with services that would throw if reached: a limited call never touches them.
    public static TheoryData<string> ReadRoutes => ["T3", "T4", "D1", "D2", "D8"];

    private static Task<IResult> Read(string route, Guid user, HttpContext http, IRateLimitService limiter) => route switch
    {
        "T3" => TaxiRequestHandlers.GetForTrip(Guid.NewGuid(), User(user), null!, null!, http, limiter, default),
        "T4" => TaxiRequestHandlers.Get(Guid.NewGuid(), User(user), null!, null!, http, limiter, default),
        "D1" => TaxiDriverRequestHandlers.ListOffers(null, User(user), null!, http, limiter, default),
        "D2" => TaxiDriverRequestHandlers.GetOffer(Guid.NewGuid(), User(user), null!, http, limiter, default),
        "D8" => TaxiDriverRequestHandlers.ListBookings(null, null, User(user), null!, null!, http, limiter, default),
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    public static TheoryData<string> ActionRoutes => ["T5", "T6", "T7", "T8", "D3", "D4", "D6"];

    private static Task<IResult> Act(string route, Guid user, HttpContext http, IRateLimitService limiter) => route switch
    {
        "T5" => TaxiRequestHandlers.Cancel(Guid.NewGuid(), new CancelTaxiRequestDto { Reason = "TravellerCancelled" }, new CancelTaxiRequestValidator(),
            User(user), null!, null!, http, limiter, default),
        "T6" => TaxiRequestHandlers.AskMore(Guid.NewGuid(), User(user), null!, null!, http, limiter, default),
        "T7" => TaxiRequestHandlers.Select(Guid.NewGuid(), Guid.NewGuid(), User(user), null!, null!, http, limiter, default),
        "T8" => TaxiRequestHandlers.NotAgreed(Guid.NewGuid(), Guid.NewGuid(), User(user), null!, null!, http, limiter, default),
        "D3" => TaxiDriverRequestHandlers.Accept(Guid.NewGuid(), null, User(user), null!, null!, http, limiter, default),
        "D4" => TaxiDriverRequestHandlers.Decline(Guid.NewGuid(), null, User(user), null!, null!, http, limiter, default),
        "D6" => TaxiDriverRequestHandlers.Withdraw(Guid.NewGuid(), null, User(user), null!, null!, http, limiter, default),
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task Read_route_answers_429_after_60_reads_a_minute(string route)
    {
        var limiter = new InMemoryRateLimitService();
        var user = Guid.NewGuid();
        await SpendAsync(limiter, TaxiRateLimits.ReadKey(user), 60, 60);

        var http = new DefaultHttpContext();
        AssertLimited(await Read(route, user, http, limiter), http);
    }

    [Theory]
    [MemberData(nameof(ActionRoutes))]
    public async Task Action_route_answers_429_after_30_actions_a_minute(string route)
    {
        var limiter = new InMemoryRateLimitService();
        var user = Guid.NewGuid();
        await SpendAsync(limiter, TaxiRateLimits.ActionKey(user), 30, 30);

        var http = new DefaultHttpContext();
        AssertLimited(await Act(route, user, http, limiter), http);
    }

    [Fact]
    public async Task Limits_are_per_user_and_reads_do_not_spend_actions()
    {
        var limiter = new InMemoryRateLimitService();
        var spent = Guid.NewGuid();
        await SpendAsync(limiter, TaxiRateLimits.ReadKey(spent), 60, 60);

        Assert.Null(await TaxiRateLimits.ReadAsync(limiter, new DefaultHttpContext(), Guid.NewGuid()));
        Assert.Equal(200, StatusOf(await TaxiRateLimits.ActionAsync(limiter, new DefaultHttpContext(), spent, () => Task.FromResult(OkResponse(new { })))));
    }

    [Fact]
    public async Task Refused_actions_give_the_permit_back_and_done_actions_keep_it()
    {
        var limiter = new InMemoryRateLimitService();
        var user = Guid.NewGuid();
        for (var i = 0; i < 40; i++)
            Assert.Equal(409, StatusOf(await TaxiRateLimits.ActionAsync(limiter, new DefaultHttpContext(), user,
                () => Task.FromResult(ErrorResponse(409, "No", "OfferStateInvalid")))));
        for (var i = 0; i < 30; i++)
            Assert.Equal(200, StatusOf(await TaxiRateLimits.ActionAsync(limiter, new DefaultHttpContext(), user, () => Task.FromResult(OkResponse(new { })))));

        var http = new DefaultHttpContext();
        AssertLimited(await TaxiRateLimits.ActionAsync(limiter, http, user, () => Task.FromResult(OkResponse(new { }))), http);
    }

    [Fact]
    public async Task An_action_that_throws_gives_the_permit_back()
    {
        var limiter = new InMemoryRateLimitService();
        var user = Guid.NewGuid();
        await SpendAsync(limiter, TaxiRateLimits.ActionKey(user), 30, 29);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TaxiRateLimits.ActionAsync(limiter, new DefaultHttpContext(), user, () => throw new InvalidOperationException()));

        Assert.Equal(200, StatusOf(await TaxiRateLimits.ActionAsync(limiter, new DefaultHttpContext(), user, () => Task.FromResult(OkResponse(new { })))));
    }

    [Fact]
    public void Numbers_are_the_owner_numbers()
        => Assert.Equal((60, 30, TimeSpan.FromMinutes(1)),
            (Core.Models.TaxiRequests.TaxiDispatchRules.ReadsPerMinute, Core.Models.TaxiRequests.TaxiDispatchRules.ActionsPerMinute, TaxiRateLimits.Window));
}
