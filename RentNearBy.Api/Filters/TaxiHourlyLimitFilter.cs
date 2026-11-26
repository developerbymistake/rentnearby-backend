using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Filters;

// SPEC 6, contract 1.2: one rolling-hour bucket per driver, taken by ONE atomic IRateLimitService.CheckAsync (Lua sliding log).
// Outermost endpoint filter (after authorization). Denied = handler not run, nothing taken. Any non-2xx answer or an exception
// gives the permit back. A limiter error fails open inside the limiter (warning log); a failed give-back is logged and ignored.
public abstract class TaxiHourlyLimitFilter(IRateLimitService limiter, ILogger logger) : IEndpointFilter
{
    protected abstract string Bucket { get; }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!OwnerHttp.TryOwner(http.User, out var userId)) return await next(context);

        var lease = new RateLimitPermits(limiter);
        var taken = await lease.TakeAsync(TaxiHourlyLimits.KeyOf(Bucket, userId), TaxiHourlyLimits.Max, TaxiHourlyLimits.Window);
        if (!taken.IsAllowed) return Denied(http, Bucket, taken.RetryAfter);

        object? raw;
        try
        {
            raw = await next(context);
        }
        catch
        {
            await ReleaseAsync(lease);
            throw;
        }
        // A binding failure sets the 400 on the response and returns Results.Empty, so the status is checked too.
        if ((raw is IResult result && !OwnerHttp.IsSuccess(result)) || http.Response.StatusCode >= 400) await ReleaseAsync(lease);
        return raw;
    }

    public static IResult Denied(HttpContext http, string bucket, TimeSpan? retryAfter)
    {
        var seconds = TaxiHourlyLimits.RetrySeconds(retryAfter);
        http.Response.Headers["Retry-After"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ErrorResponse(429, TaxiHourlyLimits.Message, OwnerErrorTypes.TooManyRequests,
            new TaxiHourlyRefusalDto(seconds, bucket, TaxiHourlyLimits.Max));
    }

    private async Task ReleaseAsync(RateLimitPermits lease)
    {
        try { await lease.ReleaseAllAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "taxi.hourly.release failed {Bucket}", Bucket); }
    }
}

public sealed record TaxiHourlyRefusalDto(int RetryAfterSeconds, string Bucket, int Limit);

public sealed class AvailabilityHourLimitFilter(IRateLimitService limiter, ILogger<AvailabilityHourLimitFilter> logger)
    : TaxiHourlyLimitFilter(limiter, logger)
{
    protected override string Bucket => TaxiHourlyLimits.AvailabilityBucket;
}

public sealed class BookingHourLimitFilter(IRateLimitService limiter, ILogger<BookingHourLimitFilter> logger)
    : TaxiHourlyLimitFilter(limiter, logger)
{
    protected override string Bucket => TaxiHourlyLimits.BookingsBucket;
}

public sealed record TaxiHourlyBucketMetadata(string Bucket);

public static class TaxiHourlyLimitExtensions
{
    // Bucket A: PUT /taxis/me/days-off only.
    public static RouteHandlerBuilder AvailabilityHourLimited(this RouteHandlerBuilder builder)
        => builder.WithMetadata(new TaxiHourlyBucketMetadata(TaxiHourlyLimits.AvailabilityBucket)).AddEndpointFilter<AvailabilityHourLimitFilter>();

    // Bucket B: the four driver calendar writes (create, update, delete, money).
    public static RouteHandlerBuilder BookingHourLimited(this RouteHandlerBuilder builder)
        => builder.WithMetadata(new TaxiHourlyBucketMetadata(TaxiHourlyLimits.BookingsBucket)).AddEndpointFilter<BookingHourLimitFilter>();
}
