using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.TaxiRequests;

// ListingId = the caller's live listing; PoolInputsChanged = the report moved a pool input (RingReadyAt was null or
// RingNotificationsOn flipped).
public sealed record DriverReportStored(Guid ListingId, bool PoolInputsChanged);

// The database side of a device report (D11), so the reset rule runs the same against Postgres and in tests.
public interface IDriverReadinessSteps
{
    // Stores the report on the caller's live listing; null = the caller has no live taxi listing.
    Task<DriverReportStored?> StoreReportAsync(Guid driverUserId, DriverDeviceStateDto report, DateTime nowUtc, CancellationToken ct);

    // True when the counter was above 0.
    Task<bool> ResetRingMissAsync(Guid driverListingId, CancellationToken ct);

    // After a write that changed a pool input: pickup driver counts and the matcher see the driver again.
    Task AfterReportAsync(Guid driverListingId);
}

// Contract section 6, missed rings rule (P26): only a report sent right after a shown test ring, with notifications and the
// ring channel on, brings the counter back to 0. A plain report never does. The global pickup cache and the matcher are
// touched only when the report changed something the pool reads, so a repeated report costs one row write.
public static class DriverReadinessFlow
{
    public static bool ResetsMissedRings(DriverDeviceStateDto report) => report.RingTested && report.NotificationsOn && report.RingChannelOn;

    public static bool PoolInputsChanged(bool wasReady, bool wasNotificationsOn, DriverDeviceStateDto report)
        => !wasReady || wasNotificationsOn != report.NotificationsOn;

    public static async Task<Guid?> ReportAsync(IDriverReadinessSteps steps, Guid driverUserId, DriverDeviceStateDto report, DateTime nowUtc, CancellationToken ct)
    {
        var stored = await steps.StoreReportAsync(driverUserId, report, nowUtc, ct);
        if (stored is null) return null;
        var reset = ResetsMissedRings(report) && await steps.ResetRingMissAsync(stored.ListingId, ct);
        if (stored.PoolInputsChanged || reset) await steps.AfterReportAsync(stored.ListingId);
        return stored.ListingId;
    }
}

public sealed class EfDriverReadinessSteps(ApplicationDbContext db, TaxiRequestStore store, TaxiPickupPlaces places, IMatcherSignal matcher)
    : IDriverReadinessSteps
{
    public async Task<DriverReportStored?> StoreReportAsync(Guid driverUserId, DriverDeviceStateDto report, DateTime nowUtc, CancellationToken ct)
    {
        var before = await db.TaxiListings.AsNoTracking().Where(l => l.UserId == driverUserId && !l.IsDeleted)
            .Select(l => new { l.Id, WasReady = l.RingReadyAt != null, l.RingNotificationsOn }).FirstOrDefaultAsync(ct);
        if (before is null) return null;
        await db.TaxiListings.Where(l => l.Id == before.Id).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.RingReadyAt, nowUtc)
            .SetProperty(l => l.RingReadyBuild, report.AppBuild)
            .SetProperty(l => l.RingNotificationsOn, report.NotificationsOn), ct);
        return new DriverReportStored(before.Id, DriverReadinessFlow.PoolInputsChanged(before.WasReady, before.RingNotificationsOn, report));
    }

    public Task<bool> ResetRingMissAsync(Guid driverListingId, CancellationToken ct) => store.ResetRingMissAsync(db, driverListingId, ct);

    public async Task AfterReportAsync(Guid driverListingId)
    {
        await places.BumpAsync();
        matcher.MarkDriverFreed(driverListingId);
    }
}

// Driver side of the taxi request system (contract section 6, D1 to D12). The offer or booking must belong to the caller
// (403 NotOfferDriver, unknown id 404 OfferNotFound); the services re-check inside their own transactions.
public static class TaxiDriverRequestHandlers
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    // Ring tag "taxi_ring_{offerId}" with the empty offer id of a test ring.
    public const string TestRingTag = "taxi_ring_";

    public static int StatusOf(string errorType) => errorType switch
    {
        TaxiRequestErrorTypes.ReasonRequired => 400,
        TaxiRequestErrorTypes.NotOfferDriver => 403,
        TaxiRequestErrorTypes.OfferNotFound => 404,
        TaxiRequestErrorTypes.TooManyRequests => 429,
        _ => 409,
    };

    public static IResult Refusal(TaxiActionResult result)
    {
        var type = result.Error!;
        return ErrorResponse(StatusOf(type), MessageOf(type, result.Data), type, result.Data);
    }

    public static string MessageOf(string type, object? data)
    {
        if (type == TaxiRequestErrorTypes.DatesAlreadyBooked && data is not null)
        {
            var e = JsonSerializer.SerializeToElement(data);
            if (e.TryGetProperty("from", out var f) && e.TryGetProperty("to", out var t)
                && DateOnly.TryParse(f.GetString(), out var from) && DateOnly.TryParse(t.GetString(), out var to))
                return TaxiRequestErrorTypes.DatesAlreadyBookedMessage(from, to);
        }
        return TaxiRequestErrorTypes.Messages.GetValueOrDefault(type) ?? type;
    }

    private static IResult Refuse(string type) => Refusal(TaxiActionResult.Fail(type));

    // D1
    public static async Task<IResult> ListOffers(string? cursor, ClaimsPrincipal principal, ApplicationDbContext db, HttpContext http,
        IRateLimitService limiter, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await TaxiRateLimits.ReadAsync(limiter, http, userId) is { } limited) return limited;
        DriverOfferCursor? position = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!DriverOfferCursor.TryParse(cursor, out var parsed)) return BadRequestResponse("Invalid cursor");
            position = parsed;
        }
        return OkResponse(await TaxiDriverViews.LoadListAsync(db, userId, position, DateTime.UtcNow, ct));
    }

    // D2
    public static async Task<IResult> GetOffer(Guid offerId, ClaimsPrincipal principal, ApplicationDbContext db, HttpContext http,
        IRateLimitService limiter, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await TaxiRateLimits.ReadAsync(limiter, http, userId) is { } limited) return limited;
        if (await CheckOfferAsync(db, offerId, userId, ct) is { } denied) return denied;
        return await OfferResponseAsync(db, offerId, withTrip: true, ct, http.RequestServices);
    }

    // D3
    public static async Task<IResult> Accept(Guid offerId, AcceptOfferDto? body, ClaimsPrincipal principal, ApplicationDbContext db, TaxiOfferService offers,
        HttpContext http, IRateLimitService limiter, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        return await TaxiRateLimits.ActionAsync(limiter, http, userId, async () =>
        {
            if (await CheckOfferAsync(db, offerId, userId, ct) is { } denied) return denied;
            var result = await offers.AcceptAsync(offerId, userId, body?.DeviceToken, ct);
            return result.Ok ? await OfferResponseAsync(db, offerId, withTrip: true, ct, http.RequestServices) : Refusal(result);
        });
    }

    // D4
    public static async Task<IResult> Decline(Guid offerId, DeclineOfferDto? body, ClaimsPrincipal principal, ApplicationDbContext db,
        TaxiOfferService offers, HttpContext http, IRateLimitService limiter, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        return await TaxiRateLimits.ActionAsync(limiter, http, userId, async () =>
        {
            if (await CheckOfferAsync(db, offerId, userId, ct) is { } denied) return denied;
            var result = await offers.DeclineAsync(offerId, userId, body?.Chip, body?.DeviceToken, ct);
            return result.Ok ? await OfferResponseAsync(db, offerId, withTrip: false, ct) : Refusal(result);
        });
    }

    // D5
    public static async Task<IResult> Confirm(Guid offerId, ClaimsPrincipal principal, ApplicationDbContext db, TaxiBookingService bookings, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await CheckOfferAsync(db, offerId, userId, ct) is { } denied) return denied;
        var result = await bookings.ConfirmAsync(offerId, userId, ct);
        return result.Ok ? await OfferResponseAsync(db, offerId, withTrip: false, ct) : Refusal(result);
    }

    // D6
    public static async Task<IResult> Withdraw(Guid offerId, WithdrawOfferDto? body, ClaimsPrincipal principal, ApplicationDbContext db,
        TaxiOfferService offers, HttpContext http, IRateLimitService limiter, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        return await TaxiRateLimits.ActionAsync(limiter, http, userId, async () =>
        {
            if (await CheckOfferAsync(db, offerId, userId, ct) is { } denied) return denied;
            var result = await offers.WithdrawAsync(offerId, userId, body?.Chip, ct);
            return result.Ok ? await OfferResponseAsync(db, offerId, withTrip: false, ct) : Refusal(result);
        });
    }

    // D7
    public static async Task<IResult> EndBooking(Guid bookingId, ClaimsPrincipal principal, ApplicationDbContext db, TaxiBookingService bookings, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var owners = await db.Set<TaxiBooking>().AsNoTracking().Where(b => b.Id == bookingId).Select(b => b.DriverUserId).Take(1).ToListAsync(ct);
        if (OwnerCheck(owners, userId) is { } denied) return denied;
        var result = await bookings.EndByDriverAsync(bookingId, userId, ct);
        if (!result.Ok) return Refusal(result);
        var dto = await TaxiDriverViews.LoadBookingAsync(db, bookingId, ct);
        return dto is null ? Refuse(TaxiRequestErrorTypes.OfferNotFound) : OkResponse(dto);
    }

    // D8
    public static async Task<IResult> ListBookings(DateOnly? from, DateOnly? to, ClaimsPrincipal principal, ApplicationDbContext db,
        IValidator<DriverBookingsWindow> validator, HttpContext http, IRateLimitService limiter, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await TaxiRateLimits.ReadAsync(limiter, http, userId) is { } limited) return limited;
        var window = new DriverBookingsWindow(from, to);
        var validation = await validator.ValidateAsync(window, ct);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        return OkResponse(await TaxiDriverViews.LoadBookingsAsync(db, userId, from!.Value, to!.Value, ct));
    }

    // D9
    public static async Task<IResult> Rating(ClaimsPrincipal principal, ApplicationDbContext db, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        return OkResponse(await TaxiDriverViews.LoadRatingAsync(db, userId, ct));
    }

    // D10
    public static async Task<IResult> GetReadiness(ClaimsPrincipal principal, ApplicationDbContext db, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        return OkResponse(await TaxiDriverViews.LoadReadinessAsync(db, userId, ct));
    }

    public static string ReadinessRateKey(Guid userId) => $"taxi:readiness:{userId}";

    // D11: 20 reports per hour per driver; this report is the only driver write that can reset the missed rings.
    public static async Task<IResult> PutReadiness(DriverDeviceStateDto body, IValidator<DriverDeviceStateDto> validator, ClaimsPrincipal principal,
        HttpContext http, ApplicationDbContext db, TaxiRequestStore store, TaxiPickupPlaces places, IMatcherSignal matcher,
        IRateLimitService rateLimiter, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(body, ct);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        if (await LimitedAsync(rateLimiter, http, ReadinessRateKey(userId), TaxiDispatchRules.ReadinessReportsPerHour) is { } limited) return limited;

        var steps = new EfDriverReadinessSteps(db, store, places, matcher);
        if (await DriverReadinessFlow.ReportAsync(steps, userId, body, DateTime.UtcNow, ct) is null)
            return NotFoundResponse("Taxi not found");
        return OkResponse(await TaxiDriverViews.LoadReadinessAsync(db, userId, ct));
    }

    // D12: one test ring to the caller's devices, 6 per hour.
    public static async Task<IResult> TestRing(ClaimsPrincipal principal, HttpContext http, ApplicationDbContext db, IRateLimitService rateLimiter,
        IOutboxSignal outbox, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await LimitedAsync(rateLimiter, http, $"taxi:testring:{userId}", TaxiDispatchRules.TestRingsPerHour) is { } limited) return limited;

        db.OutboxMessages.Add(TestRingOutbox(userId, DateTime.UtcNow));
        await db.SaveChangesAsync(ct);
        outbox.Wake();
        return NoContentResponse();
    }

    // Hourly limit per key: null = allowed, else 429 with Retry-After.
    private static async Task<IResult?> LimitedAsync(IRateLimitService rateLimiter, HttpContext http, string key, int perHour)
    {
        var limit = await rateLimiter.CheckAsync(key, perHour, Hour);
        if (limit.IsAllowed) return null;
        http.Response.Headers["Retry-After"] = ((int)Math.Ceiling((limit.RetryAfter ?? Hour).TotalSeconds)).ToString();
        return TooManyRequestsResponse();
    }

    // Contract 10.1: the ring shape with kind taxi_ring_test, offerId empty and expiresAt 30 seconds ahead.
    public static OutboxMessage TestRingOutbox(Guid userId, DateTime nowUtc)
    {
        var expires = nowUtc.AddSeconds(TaxiDispatchRules.TestRingSeconds);
        var data = TestRingData(nowUtc, expires, userId);
        var push = new OutboxPushPayload(
            "ringtest:" + Guid.NewGuid().ToString("N"), userId, null, data["title"], data["line1"], data["action_route"], data["action_args_json"],
            TestRingTag, 0, NotificationTypes.TaxiRequestRing, expires, data);
        return new OutboxMessage
        {
            Key = push.Key,
            Exchange = OutboxTargets.PushExchange,
            RoutingKey = OutboxTargets.PushRoutingKey,
            Payload = JsonSerializer.Serialize(push, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = nowUtc,
            NextAttemptAt = nowUtc,
        };
    }

    public static Dictionary<string, string> TestRingData(DateTime nowUtc, DateTime expiresAtUtc, Guid userId) => new()
    {
        ["notification_type"] = NotificationTypes.ToWireValue(NotificationTypes.TaxiRequestRing),
        ["kind"] = TaxiPushKinds.RingTest,
        ["v"] = TaxiPushKinds.PayloadVersion,
        ["offerId"] = "",
        ["requestId"] = "",
        ["expiresAt"] = TaxiRingPayload.Instant(expiresAtUtc),
        ["sentAt"] = TaxiRingPayload.Instant(nowUtc),
        ["attempt"] = "0",
        ["ackToken"] = "",
        ["title"] = "Test ring",
        ["line1"] = "This is how a new request rings",
        ["line2"] = "",
        ["line3"] = "",
        ["sections"] = "",
        ["sightseeingDays"] = "0",
        ["action_route"] = UserNotificationRoutes.TaxiReady,
        ["action_args_json"] = "{}",
        [TaxiRingPayload.DriverUserIdKey] = userId.ToString("D"),
    };

    // Owner of the offer or booking (null = no such row) against the caller: null = allowed, else the error type.
    public static string? OwnerRefusal(Guid? ownerUserId, Guid callerUserId) =>
        ownerUserId is not { } owner ? TaxiRequestErrorTypes.OfferNotFound
        : owner == callerUserId ? null
        : TaxiRequestErrorTypes.NotOfferDriver;

    private static IResult? OwnerCheck(List<Guid> owners, Guid userId) =>
        OwnerRefusal(owners.Count == 0 ? null : owners[0], userId) is { } type ? Refuse(type) : null;

    private static async Task<IResult?> CheckOfferAsync(ApplicationDbContext db, Guid offerId, Guid userId, CancellationToken ct)
    {
        var owners = await db.Set<TaxiRequestOffer>().AsNoTracking().Where(o => o.Id == offerId).Select(o => o.DriverUserId).Take(1).ToListAsync(ct);
        return OwnerCheck(owners, userId);
    }

    private static async Task<IResult> OfferResponseAsync(ApplicationDbContext db, Guid offerId, bool withTrip, CancellationToken ct,
        IServiceProvider? services = null)
    {
        var dto = await TaxiDriverViews.LoadOfferAsync(db, offerId, withTrip, ct);
        if (dto is null) return Refuse(TaxiRequestErrorTypes.OfferNotFound);
        // UC-T-38 / 7.4: the dates of his own manual trips inside the request's dates (amber line; a manual trip never blocks a ring).
        if (withTrip && services?.GetService<RentNearBy.Infrastructure.Services.Owner.IOwnerCalendarSync>() is { } calendar
            && dto.State is OfferStates.Ringing or OfferStates.Accepted or OfferStates.Selected)
        {
            var listingId = await db.Set<TaxiRequestOffer>().AsNoTracking().Where(o => o.Id == offerId).Select(o => o.DriverListingId).FirstAsync(ct);
            dto.ManualTripOn = [.. await calendar.ManualDatesAsync(db, listingId, dto.DateFrom, dto.DateTo, ct)];
        }
        return OkResponse(dto);
    }
}
