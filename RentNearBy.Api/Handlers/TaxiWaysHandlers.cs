using System.Globalization;
using System.Security.Claims;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Read side of "ways to go". Routes require a logged-in caller (phones and stay owner numbers are returned).
public static class TaxiWaysHandlers
{
    private static readonly TimeSpan SearchWindow = TimeSpan.FromMinutes(1);
    private const int MaxPage = 1000;
    private static readonly short[] AllowedMinSeats = [4, 6, 7, 12];

    private static IResult Unavailable() =>
        ErrorResponse(503, "Ways to go are not available right now", TaxiWaysErrorTypes.TaxiWaysUnavailable);

    private static async Task<IResult?> RateLimitedAsync(Guid userId, IRateLimitService rateLimiter, HttpContext httpContext, int maxPerMinute)
    {
        var rl = await rateLimiter.CheckAsync($"taxi:ways:search:{userId}", maxPerMinute, SearchWindow);
        if (rl.IsAllowed) return null;
        httpContext.Response.Headers["Retry-After"] = ((int)(rl.RetryAfter ?? SearchWindow).TotalSeconds).ToString(CultureInfo.InvariantCulture);
        return TooManyRequestsResponse();
    }

    private static bool PeopleValid(int? people, TaxiWaysOptions o, out int value)
    {
        value = people ?? o.DefaultPeople;
        return value >= 1 && value <= o.MaxPeople;
    }

    private static bool DateValid(string? date, TaxiWaysOptions o)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
        var today = TaxiIdentityRules.TodayIst();
        return d >= today && d <= today.AddDays(o.MaxDaysAhead);
    }

    private static string? CleanName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static async Task<IResult> GetWays(
        double? fromLat, double? fromLng, double? toLat, double? toLng, string? date, int? people,
        string? fromName, string? toName,
        ITaxiWaysSearch search, IRateLimitService rateLimiter, ClaimsPrincipal principal, HttpContext httpContext,
        Microsoft.Extensions.Options.IOptions<TaxiWaysOptions> options, CancellationToken ct, bool fromMyLocation = false)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var limited = await RateLimitedAsync(userId, rateLimiter, httpContext, options.Value.SearchPerMinute);
        if (limited != null) return limited;

        if (fromLat is not (>= -90 and <= 90) || toLat is not (>= -90 and <= 90)
            || fromLng is not (>= -180 and <= 180) || toLng is not (>= -180 and <= 180))
            return BadRequestResponse("Invalid coordinates");
        if (!DateValid(date, options.Value)) return BadRequestResponse("Pick a date within the next 90 days", TaxiWaysErrorTypes.InvalidDate);
        if (!PeopleValid(people, options.Value, out var count)) return BadRequestResponse("Invalid number of people");
        var from = CleanName(fromName);
        var to = CleanName(toName);
        if (from?.Length > TaxiLimits.PlaceNameMax || to?.Length > TaxiLimits.PlaceNameMax)
            return BadRequestResponse("Place name is too long");

        var result = await search.SearchAsync(
            new TaxiWaysQuery(fromLat.Value, fromLng.Value, toLat.Value, toLng.Value, count, fromMyLocation, from, to), ct);
        return result == null ? Unavailable() : OkResponse(result);
    }

    public static async Task<IResult> GetPlan(
        string planKey, int? people, string? date,
        ITaxiWaysSearch search, IRateLimitService rateLimiter, ClaimsPrincipal principal, HttpContext httpContext,
        Microsoft.Extensions.Options.IOptions<TaxiWaysOptions> options, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var limited = await RateLimitedAsync(userId, rateLimiter, httpContext, options.Value.SearchPerMinute);
        if (limited != null) return limited;

        if (!PeopleValid(people, options.Value, out var count)) return BadRequestResponse("Invalid number of people");
        if (date != null && !DateValid(date, options.Value)) return BadRequestResponse("Pick a date within the next 90 days", TaxiWaysErrorTypes.InvalidDate);
        if (!TaxiWaysKeys.IsValidKey(planKey)) return PlanNotFound();

        try
        {
            var plan = await search.PlanAsync(planKey, count, ct);
            return plan == null ? PlanNotFound() : OkResponse(plan);
        }
        catch (TaxiWaysUnavailableException)
        {
            return Unavailable();
        }
    }

    public static async Task<IResult> GetLegDrivers(
        string legKey, int? people, short? minSeats, string? category, bool? repliesInApp, string? sort,
        ITaxiWaysSearch search, IRateLimitService rateLimiter, ClaimsPrincipal principal, HttpContext httpContext,
        Microsoft.Extensions.Options.IOptions<TaxiWaysOptions> options, CancellationToken ct, int page = 1, int pageSize = 0)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var o = options.Value;
        var limited = await RateLimitedAsync(userId, rateLimiter, httpContext, o.SearchPerMinute);
        if (limited != null) return limited;

        if (!PeopleValid(people, options.Value, out var count)) return BadRequestResponse("Invalid number of people");
        if (minSeats.HasValue && Array.IndexOf(AllowedMinSeats, minSeats.Value) < 0) return BadRequestResponse("Invalid seats filter");
        if (category != null && !TaxiVehicleCategories.All.Contains(category)) return BadRequestResponse("Invalid car type");
        var sortValue = sort ?? TaxiWaysSorts.BestFit;
        if (!TaxiWaysSorts.IsValid(sortValue)) return BadRequestResponse("Invalid sort");
        if (page is < 1 or > MaxPage) return BadRequestResponse("Invalid page");
        var size = pageSize == 0 ? o.LegPageSize : pageSize;
        if (size < 1 || size > o.LegPageSizeMax) return BadRequestResponse("Invalid page size");
        if (!TaxiWaysKeys.IsValidKey(legKey)) return LegNotFound();

        try
        {
            var result = await search.LegDriversAsync(
                legKey, new LegDriverQuery(count, minSeats, category, repliesInApp == true, sortValue, page, size), ct);
            return result == null ? LegNotFound() : OkResponse(result);
        }
        catch (TaxiWaysUnavailableException)
        {
            return Unavailable();
        }
    }

    private static IResult PlanNotFound() =>
        ErrorResponse(404, "This plan is not available any more", TaxiWaysErrorTypes.PlanNotFound);

    private static IResult LegNotFound() =>
        ErrorResponse(404, "No driver on this stretch any more", TaxiWaysErrorTypes.LegNotFound);
}
