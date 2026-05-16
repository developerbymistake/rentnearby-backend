using Microsoft.Extensions.Caching.Distributed;
using System.Security.Claims;

namespace RentNearBy.Api.Middleware;

public class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitingMiddleware> _logger;
    private const string RateLimitKeyPrefix = "ratelimit:";
    private const string FailedAttemptsKeyPrefix = "failed_attempts:";
    private const int MaxRequestsPerMinute = 10;
    private const int MaxFailedAttempts = 5;
    private const int FailedAttemptLockoutMinutes = 15;

    public RateLimitingMiddleware(RequestDelegate next, ILogger<RateLimitingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IDistributedCache? cache)
    {
        // Only rate limit payment endpoints
        if (!IsPaymentEndpoint(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (cache == null)
        {
            await _next(context);
            return;
        }

        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            await _next(context);
            return;
        }

        // Check for lockout due to failed attempts
        var failedAttemptsKey = $"{FailedAttemptsKeyPrefix}{userId}";
        var failedAttemptsBytes = await cache.GetAsync(failedAttemptsKey);
        if (failedAttemptsBytes != null)
        {
            var failedCount = int.Parse(System.Text.Encoding.UTF8.GetString(failedAttemptsBytes));
            if (failedCount >= MaxFailedAttempts)
            {
                _logger.LogWarning($"User {userId} locked out due to failed payment attempts");
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new
                {
                    Status = "429",
                    Code = 429,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Error = new { Message = "Too many failed attempts. Please try again later.", Type = "TooManyRequests" }
                });
                return;
            }
        }

        // Check rate limit: requests per minute
        var rateLimitKey = $"{RateLimitKeyPrefix}{userId}";
        var requestCountBytes = await cache.GetAsync(rateLimitKey);
        var requestCount = 0;

        if (requestCountBytes != null)
        {
            requestCount = int.Parse(System.Text.Encoding.UTF8.GetString(requestCountBytes));
        }

        if (requestCount >= MaxRequestsPerMinute)
        {
            _logger.LogWarning($"User {userId} exceeded rate limit: {requestCount}/{MaxRequestsPerMinute} requests");
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new
            {
                Status = "429",
                Code = 429,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Error = new { Message = "Too many requests. Please try again later.", Type = "TooManyRequests" }
            });
            return;
        }

        // Increment request count
        await cache.SetAsync(rateLimitKey, System.Text.Encoding.UTF8.GetBytes((requestCount + 1).ToString()),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });

        // Capture response status to track failed attempts
        var originalBodyStream = context.Response.Body;
        using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        await _next(context);

        // If response is 4xx or 5xx, increment failed attempts counter
        if (context.Response.StatusCode >= 400)
        {
            var failedCount = 0;
            if (failedAttemptsBytes != null)
                failedCount = int.Parse(System.Text.Encoding.UTF8.GetString(failedAttemptsBytes));

            failedCount++;
            await cache.SetAsync(failedAttemptsKey, System.Text.Encoding.UTF8.GetBytes(failedCount.ToString()),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(FailedAttemptLockoutMinutes) });

            _logger.LogWarning($"User {userId} failed payment attempt {failedCount}/{MaxFailedAttempts}");
        }

        // Copy response back
        await memoryStream.CopyToAsync(originalBodyStream);
    }

    private static bool IsPaymentEndpoint(PathString path)
    {
        var pathValue = path.Value?.ToLower() ?? "";
        return pathValue.Contains("/go-live") || pathValue.Contains("/verify-payment");
    }
}
