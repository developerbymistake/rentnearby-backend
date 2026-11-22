using System.Security.Claims;
using Microsoft.Net.Http.Headers;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Raw pack JSON (no envelope: a timestamp would break the immutable URL), served from memory bytes.
public static class TripMapPackHandlers
{
    private const int PackMaxPerWindow = 30;
    private static readonly TimeSpan PackWindow = TimeSpan.FromMinutes(1);
    public const string ImmutableCacheControl = "private, max-age=31536000, immutable";

    public static async Task<IResult> GetMapPack(
        Guid stateId, string version, ClaimsPrincipal principal, ITripMapPackService packs,
        IRateLimitService rateLimiter, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var rl = await rateLimiter.CheckAsync($"trip:pack:{userId}", PackMaxPerWindow, PackWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)(rl.RetryAfter ?? PackWindow).TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        if (!TripMapPackKeys.IsVersion(version)) return BadRequestResponse("Invalid map version", "BadRequest");

        var (pack, unavailable) = await packs.GetAsync(stateId, version, httpContext.RequestAborted);
        if (pack == null)
            return unavailable
                ? ErrorResponse(503, "Map data is not available right now", MapPackErrorTypes.MapPackUnavailable)
                : ErrorResponse(404, "Map data not found", MapPackErrorTypes.MapPackNotFound);

        var headers = httpContext.Response.Headers;
        var etag = $"\"{pack.Version}\"";
        headers.ETag = etag;
        headers.CacheControl = ImmutableCacheControl;
        headers.Vary = HeaderNames.AcceptEncoding;
        if (IfNoneMatches(httpContext.Request.Headers.IfNoneMatch.ToString(), etag))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        if (AcceptsGzip(httpContext.Request.Headers.AcceptEncoding.ToString()))
        {
            headers.ContentEncoding = "gzip";
            return Results.Bytes(pack.Gzip, "application/json");
        }
        return Results.Bytes(TripMapPackService.Gunzip(pack.Gzip), "application/json");
    }

    internal static bool IfNoneMatches(string header, string etag)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        foreach (var part in header.Split(','))
        {
            var tag = part.Trim();
            if (tag == "*" || tag == etag || tag == "W/" + etag) return true;
        }
        return false;
    }

    internal static bool AcceptsGzip(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        foreach (var part in header.Split(','))
        {
            var pieces = part.Split(';');
            if (!pieces[0].Trim().Equals("gzip", StringComparison.OrdinalIgnoreCase)) continue;
            var refused = pieces.Skip(1).Any(p =>
            {
                var kv = p.Trim();
                return kv.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(kv[2..], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var q) && q <= 0;
            });
            if (!refused) return true;
        }
        return false;
    }
}
