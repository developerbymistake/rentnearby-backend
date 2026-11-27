using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Stays.Quotation;

// Q21 / ARCHITECTURE 9.2: the three own buckets take a permit before the handler and never give it back, so a loop of refused
// reserves (409) still ends in 429 at the 31st call of the hour; photo 10 an hour; name change 3 a day.
public class QuoteRateLimitTests
{
    private static DefaultHttpContext Http(Guid user)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test"));
        return http;
    }

    private static async Task<(object? Result, int Runs)> Run(IEndpointFilter filter, HttpContext http, Func<IResult> handler)
    {
        var runs = 0;
        var result = await filter.InvokeAsync(EndpointFilterInvocationContext.Create(http), _ =>
        {
            runs++;
            return ValueTask.FromResult<object?>(handler());
        });
        return (result, runs);
    }

    private static int StatusOf(object? result) => result is IStatusCodeHttpResult { StatusCode: { } code } ? code : 200;

    private static IResult MonthLimit() => QuoteHttp.Refuse(RentNearBy.Core.Models.Owner.Quote.QuoteErrors.MonthLimit);

    [Fact]
    public async Task Thirty_first_reserve_in_an_hour_is_429_even_when_the_earlier_ones_were_refused()
    {
        var limiter = new InMemoryRateLimitService();
        var filter = new QuoteReserveLimitFilter(limiter);
        var me = Guid.NewGuid();
        for (var i = 0; i < 30; i++)
        {
            var (result, runs) = await Run(filter, Http(me), MonthLimit);
            Assert.Equal((409, 1), (StatusOf(result), runs));
        }
        var http = Http(me);
        var (last, lastRuns) = await Run(filter, http, MonthLimit);
        Assert.Equal((429, 0), (StatusOf(last), lastRuns));
        Assert.True(int.Parse(http.Response.Headers.RetryAfter.ToString()) >= 1);
    }

    [Fact]
    public async Task Another_owner_has_his_own_bucket()
    {
        var limiter = new InMemoryRateLimitService();
        var filter = new QuoteReserveLimitFilter(limiter);
        var me = Guid.NewGuid();
        for (var i = 0; i < 30; i++) await Run(filter, Http(me), () => Results.Ok());
        Assert.Equal(200, StatusOf((await Run(filter, Http(Guid.NewGuid()), () => Results.Ok())).Result));
    }

    [Fact]
    public async Task Photo_is_ten_an_hour()
    {
        var limiter = new InMemoryRateLimitService();
        var filter = new QuotePhotoLimitFilter(limiter);
        var me = Guid.NewGuid();
        for (var i = 0; i < 10; i++) Assert.Equal(1, (await Run(filter, Http(me), () => Results.BadRequest())).Runs);
        Assert.Equal(429, StatusOf((await Run(filter, Http(me), () => Results.Ok())).Result));
    }

    [Fact]
    public async Task Name_change_is_three_a_day()
    {
        var limiter = new InMemoryRateLimitService();
        var filter = new QuoteNameLimitFilter(limiter);
        var me = Guid.NewGuid();
        for (var i = 0; i < 3; i++) Assert.Equal(1, (await Run(filter, Http(me), () => Results.Ok())).Runs);
        var (result, runs) = await Run(filter, Http(me), () => Results.Ok());
        Assert.Equal((429, 0), (StatusOf(result), runs));
    }

    [Fact]
    public async Task Without_a_user_the_filter_leaves_the_answer_to_auth()
    {
        var filter = new QuoteReserveLimitFilter(new InMemoryRateLimitService());
        var (_, runs) = await Run(filter, new DefaultHttpContext(), () => Results.Unauthorized());
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Refusals_use_the_owner_429_type()
        => Assert.Equal("TooManyRequests", OwnerErrorTypes.TooManyRequests);
}
