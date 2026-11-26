using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Filters;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;

namespace RentNearBy.Tests.TaxiAvailability;

// SPEC 6 / contract 1.2: two independent rolling-hour buckets, 3 writes per driver, atomic take, give back on any non-2xx.
public class TaxiHourlyLimitTests
{
    private sealed class Clock
    {
        public long Ms = 1_800_000_000_000;
    }

    private static (InMemoryRateLimitService Limiter, Clock Clock) Limiter()
    {
        var clock = new Clock();
        return (new InMemoryRateLimitService(() => Interlocked.Read(ref clock.Ms)), clock);
    }

    private static Task<RateLimitResult> Take(IRateLimitService l, string key) => l.CheckAsync(key, TaxiHourlyLimits.Max, TaxiHourlyLimits.Window);

    [Fact]
    public void Keys_are_separate_per_bucket_and_per_driver()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.NotEqual(TaxiHourlyLimits.Availability(a), TaxiHourlyLimits.Bookings(a));
        Assert.NotEqual(TaxiHourlyLimits.Availability(a), TaxiHourlyLimits.Availability(b));
        Assert.StartsWith("taxi:avail:write:", TaxiHourlyLimits.Availability(a));
        Assert.StartsWith("taxi:cal:write:", TaxiHourlyLimits.Bookings(a));
        Assert.Equal(3, TaxiHourlyLimits.Max);
        Assert.Equal(TimeSpan.FromMinutes(60), TaxiHourlyLimits.Window);
    }

    [Theory]
    [InlineData(TaxiHourlyLimits.AvailabilityBucket)]
    [InlineData(TaxiHourlyLimits.BookingsBucket)]
    public async Task Twenty_parallel_takes_allow_exactly_three_per_bucket(string bucket)
    {
        var (limiter, _) = Limiter();
        var key = TaxiHourlyLimits.KeyOf(bucket, Guid.NewGuid());
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => Take(limiter, key))));
        Assert.Equal(3, results.Count(r => r.IsAllowed));
        Assert.All(results.Where(r => !r.IsAllowed), r => Assert.InRange(r.RetryAfter!.Value.TotalSeconds, 1, 3600));
    }

    [Fact]
    public async Task Buckets_are_independent_both_ways()
    {
        var (limiter, _) = Limiter();
        var me = Guid.NewGuid();
        for (var i = 0; i < 3; i++) Assert.True((await Take(limiter, TaxiHourlyLimits.Availability(me))).IsAllowed);
        Assert.False((await Take(limiter, TaxiHourlyLimits.Availability(me))).IsAllowed);
        for (var i = 0; i < 3; i++) Assert.True((await Take(limiter, TaxiHourlyLimits.Bookings(me))).IsAllowed);
        Assert.False((await Take(limiter, TaxiHourlyLimits.Bookings(me))).IsAllowed);

        var other = Guid.NewGuid();
        for (var i = 0; i < 3; i++) Assert.True((await Take(limiter, TaxiHourlyLimits.Bookings(other))).IsAllowed);
        Assert.True((await Take(limiter, TaxiHourlyLimits.Availability(other))).IsAllowed);
    }

    [Fact]
    public async Task Driver_a_at_the_limit_never_blocks_driver_b()
    {
        var (limiter, _) = Limiter();
        var a = Guid.NewGuid();
        for (var i = 0; i < 3; i++) await Take(limiter, TaxiHourlyLimits.Availability(a));
        Assert.False((await Take(limiter, TaxiHourlyLimits.Availability(a))).IsAllowed);
        Assert.True((await Take(limiter, TaxiHourlyLimits.Availability(Guid.NewGuid()))).IsAllowed);
    }

    [Fact]
    public async Task The_fourth_passes_when_the_oldest_permit_is_sixty_minutes_old()
    {
        var (limiter, clock) = Limiter();
        var key = TaxiHourlyLimits.Availability(Guid.NewGuid());
        await Take(limiter, key);
        clock.Ms += 10 * 60_000;
        await Take(limiter, key);
        await Take(limiter, key);
        var denied = await Take(limiter, key);
        Assert.False(denied.IsAllowed);
        Assert.Equal(50 * 60, TaxiHourlyLimits.RetrySeconds(denied.RetryAfter));
        clock.Ms += 50 * 60_000;
        Assert.True((await Take(limiter, key)).IsAllowed);
    }

    // ---- The endpoint filter ----

    private static DefaultHttpContext Http(Guid? user)
    {
        var http = new DefaultHttpContext();
        if (user is { } id) http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test"));
        return http;
    }

    private static async Task<(object? Result, int Runs)> Run(TaxiHourlyLimitFilter filter, HttpContext http, Func<IResult> handler)
    {
        var runs = 0;
        var result = await filter.InvokeAsync(EndpointFilterInvocationContext.Create(http), _ =>
        {
            runs++;
            return ValueTask.FromResult<object?>(handler());
        });
        return (result, runs);
    }

    private static AvailabilityHourLimitFilter Availability(IRateLimitService l) => new(l, NullLogger<AvailabilityHourLimitFilter>.Instance);

    private static BookingHourLimitFilter Bookings(IRateLimitService l) => new(l, NullLogger<BookingHourLimitFilter>.Instance);

    [Fact]
    public async Task A_denied_call_does_not_run_the_handler_and_answers_the_contract_429()
    {
        var (limiter, _) = Limiter();
        var me = Guid.NewGuid();
        var filter = Availability(limiter);
        for (var i = 0; i < 3; i++) Assert.Equal(1, (await Run(filter, Http(me), () => Results.Ok())).Runs);

        var http = Http(me);
        var (result, runs) = await Run(filter, http, () => Results.Ok());
        Assert.Equal(0, runs);
        var (status, data, type) = Trips.CityFixture.Read((IResult)result!);
        Assert.Equal(429, status);
        Assert.Equal("TooManyRequests", type);
        var seconds = int.Parse(http.Response.Headers["Retry-After"].ToString());
        Assert.InRange(seconds, 1, 3600);
        Assert.Equal(seconds, data.GetProperty("RetryAfterSeconds").GetInt32());
        Assert.Equal("availability", data.GetProperty("Bucket").GetString());
        Assert.Equal(3, data.GetProperty("Limit").GetInt32());
    }

    [Fact]
    public async Task The_429_body_serialises_with_the_app_field_names()
    {
        var json = JsonSerializer.Serialize(new TaxiHourlyRefusalDto(1520, "bookings", 3), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("{\"retryAfterSeconds\":1520,\"bucket\":\"bookings\",\"limit\":3}", json);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_non_2xx_answer_gives_the_permit_back()
    {
        var (limiter, _) = Limiter();
        var me = Guid.NewGuid();
        var filter = Bookings(limiter);
        for (var i = 0; i < 5; i++)
            await Run(filter, Http(me), () => Results.Json(new { }, statusCode: 409));
        for (var i = 0; i < 3; i++) Assert.Equal(1, (await Run(filter, Http(me), () => Results.Ok())).Runs);
        Assert.Equal(0, (await Run(filter, Http(me), () => Results.Ok())).Runs);
    }

    [Fact]
    public async Task A_binding_failure_400_gives_the_permit_back()
    {
        var (limiter, _) = Limiter();
        var me = Guid.NewGuid();
        var filter = Bookings(limiter);
        for (var i = 0; i < 5; i++)
        {
            var http = Http(me);
            await Run(filter, http, () =>
            {
                http.Response.StatusCode = 400;
                return Results.Empty;
            });
        }
        for (var i = 0; i < 3; i++) Assert.Equal(1, (await Run(filter, Http(me), () => Results.Ok())).Runs);
        Assert.Equal(0, (await Run(filter, Http(me), () => Results.Ok())).Runs);
    }

    [Fact]
    public async Task An_inner_per_minute_429_gives_the_hourly_permit_back()
    {
        var (limiter, _) = Limiter();
        var me = Guid.NewGuid();
        var filter = Availability(limiter);
        for (var i = 0; i < 5; i++)
            await Run(filter, Http(me), () => Results.Json(new { }, statusCode: 429));
        for (var i = 0; i < 3; i++) Assert.Equal(1, (await Run(filter, Http(me), () => Results.Ok())).Runs);
        Assert.Equal(0, (await Run(filter, Http(me), () => Results.Ok())).Runs);
    }

    [Fact]
    public async Task An_exception_gives_the_permit_back_and_is_rethrown()
    {
        var (limiter, _) = Limiter();
        var me = Guid.NewGuid();
        var filter = Bookings(limiter);
        for (var i = 0; i < 4; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await Run(filter, Http(me), () => throw new InvalidOperationException()));
        for (var i = 0; i < 3; i++) Assert.Equal(1, (await Run(filter, Http(me), () => Results.Ok())).Runs);
    }

    [Fact]
    public async Task A_caller_without_a_user_id_passes_through_to_the_policy()
    {
        var (limiter, _) = Limiter();
        var filter = Availability(limiter);
        for (var i = 0; i < 6; i++) Assert.Equal(1, (await Run(filter, Http(null), () => Results.Ok())).Runs);
    }

    [Fact]
    public async Task Ten_parallel_calls_through_the_filter_give_three_2xx()
    {
        var (limiter, _) = Limiter();
        var me = Guid.NewGuid();
        var filter = Availability(limiter);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => Run(filter, Http(me), () => Results.Ok()))));
        Assert.Equal(3, results.Sum(r => r.Runs));
    }

    // Fail open: a Redis error inside the limiter allows the call (warning log in RedisRateLimitService).
    public class ThrowingMux : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
    }

    [Fact]
    public async Task A_limiter_error_fails_open()
    {
        var mux = DispatchProxy.Create<IConnectionMultiplexer, ThrowingMux>();
        var limiter = new RedisRateLimitService(mux, NullLogger<RedisRateLimitService>.Instance);
        var filter = Availability(limiter);
        var me = Guid.NewGuid();
        for (var i = 0; i < 5; i++) Assert.Equal(1, (await Run(filter, Http(me), () => Results.Ok())).Runs);
    }
}
