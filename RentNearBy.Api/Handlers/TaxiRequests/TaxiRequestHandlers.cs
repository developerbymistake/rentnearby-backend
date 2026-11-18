using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.TaxiRequests;

// Traveller side of the taxi request system (contract section 5, T1 to T10). Every handler checks that the caller owns the
// trip or the request before it validates a body or calls a service; the services re-check inside their own transactions.
public static class TaxiRequestHandlers
{
    // Contract section 5: an unknown id is 404, another traveller's is 403 NotTripOwner.
    public static async Task<IResult?> CheckTripOwnerAsync(ApplicationDbContext db, Guid tripId, Guid userId, CancellationToken ct)
    {
        var owners = await db.Trips.AsNoTracking().Where(t => t.Id == tripId).Select(t => t.OwnerUserId).Take(1).ToListAsync(ct);
        if (owners.Count == 0) return ErrorResponse(404, "Trip not found", TripErrorTypes.TripNotFound);
        return owners[0] == userId ? null : Refusal(TaxiRequestCreateRules.Error(403, TaxiRequestErrorTypes.NotTripOwner));
    }

    private static IResult Refusal(TaxiRequestError e, TimeSpan? retryAfter = null, HttpContext? http = null)
    {
        if (retryAfter is { } wait && http is not null)
            http.Response.Headers["Retry-After"] = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
        return ErrorResponse(e.Status, e.Message, e.Type, e.Data);
    }

    // Validation errors carry the contract error type in the FluentValidation error code; any other rule is a plain BadRequest.
    public static async Task<IResult?> ValidateAsync<T>(IValidator<T> validator, T body)
    {
        var result = await validator.ValidateAsync(body);
        if (result.IsValid) return null;
        var first = result.Errors[0];
        var type = TaxiRequestErrorTypes.All.Contains(first.ErrorCode) ? first.ErrorCode : TaxiRequestCreateRules.BadRequestType;
        return BadRequestResponse(first.ErrorMessage, type);
    }

    // The statuses of the contract error table for a TaxiActionResult error (the service names only the type).
    public static int StatusOf(string errorType) => errorType switch
    {
        TaxiRequestErrorTypes.NotTripOwner or TaxiRequestErrorTypes.NotOfferDriver => 403,
        TaxiRequestErrorTypes.TaxiRequestNotFound or TaxiRequestErrorTypes.OfferNotFound => 404,
        _ => 409,
    };

    public static IResult ActionRefusal(TaxiActionResult result)
    {
        var type = result.Error!;
        return ErrorResponse(StatusOf(type), TaxiRequestErrorTypes.Messages.GetValueOrDefault(type) ?? type, type, result.Data);
    }

    private static async Task<IResult> RespondAsync(TaxiRequestResult result, TaxiRequestViews views, HttpContext http, CancellationToken ct)
    {
        if (!result.IsOk) return Refusal(result.Error!, result.RetryAfter, http);
        var dto = await views.BuildAsync(result.Request!, ct);
        return result.Created ? CreatedResponse(dto, $"/api/v1/taxi-requests/{dto.Id}") : OkResponse(dto);
    }

    // Names of the first and last city of the plan (the same keys the create check uses).
    public static (TaxiCityRefDto? First, TaxiCityRefDto? Last) EndCityRefs(TripPlan plan)
    {
        var (first, last) = TaxiRequestService.EndCities(plan);
        TaxiCityRefDto? Ref(long key)
            => key == 0 ? null : new TaxiCityRefDto { CityKey = key, Name = plan.Legs?.FirstOrDefault(l => l.CityKey == key)?.CityName ?? plan.CityName };
        return (Ref(first), Ref(last));
    }

    // T1
    public static async Task<IResult> PickupPlaces(
        Guid tripId, ClaimsPrincipal principal, ApplicationDbContext db, TaxiPickupPlaces places, ITripExecutionFeatureReader gate, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await CheckTripOwnerAsync(db, tripId, userId, ct) is { } denied) return denied;

        var trip = await db.Trips.AsNoTracking().FirstAsync(t => t.Id == tripId, ct);
        var plan = TripPlanReader.Read(trip);
        var (first, last) = TaxiRequestService.EndCities(plan);
        var (firstRef, lastRef) = EndCityRefs(plan);
        var lists = await places.GetAsync(first, last, ct);
        var dto = TaxiPickupPlaces.ToDto(lists, firstRef, lastRef, await gate.IsEnabledAsync(), TaxiRequestCreateRules.TravellersOf(plan.Settings));
        return OkResponse(dto);
    }

    // T2
    public static async Task<IResult> Create(
        Guid tripId, CreateTaxiRequestDto body, IValidator<CreateTaxiRequestDto> validator, ClaimsPrincipal principal,
        ApplicationDbContext db, TaxiRequestService service, TaxiRequestViews views, HttpContext http, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await CheckTripOwnerAsync(db, tripId, userId, ct) is { } denied) return denied;
        if (await ValidateAsync(validator, body) is { } invalid) return invalid;
        return await RespondAsync(await service.CreateAsync(tripId, userId, body, ct), views, http, ct);
    }

    // T3
    public static async Task<IResult> GetForTrip(
        Guid tripId, ClaimsPrincipal principal, ApplicationDbContext db, TaxiRequestViews views, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await CheckTripOwnerAsync(db, tripId, userId, ct) is { } denied) return denied;
        var request = await views.CurrentForTripAsync(tripId, ct);
        return OkResponse(request is null ? null : await views.BuildAsync(request, ct));
    }

    // T4
    public static async Task<IResult> Get(
        Guid requestId, ClaimsPrincipal principal, TaxiRequestService service, TaxiRequestViews views, HttpContext http, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        return await RespondAsync(await service.OwnedAsync(requestId, userId, ct), views, http, ct);
    }

    // T5
    public static async Task<IResult> Cancel(
        Guid requestId, CancelTaxiRequestDto body, IValidator<CancelTaxiRequestDto> validator, ClaimsPrincipal principal,
        TaxiRequestService service, TaxiRequestViews views, HttpContext http, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await service.OwnedAsync(requestId, userId, ct) is { IsOk: false } denied) return Refusal(denied.Error!);
        if (await ValidateAsync(validator, body) is { } invalid) return invalid;
        return await RespondAsync(await service.CancelAsync(requestId, userId, body.Reason, body.Chip, ct), views, http, ct);
    }

    // T6
    public static async Task<IResult> AskMore(
        Guid requestId, ClaimsPrincipal principal, TaxiRequestService service, TaxiRequestViews views, HttpContext http, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await service.OwnedAsync(requestId, userId, ct) is { IsOk: false } denied) return Refusal(denied.Error!);
        return await RespondAsync(await service.AskMoreAsync(requestId, userId, ct), views, http, ct);
    }

    // T7
    public static async Task<IResult> Select(
        Guid requestId, Guid offerId, ClaimsPrincipal principal, TaxiRequestService service, TaxiRequestViews views, HttpContext http, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await service.OwnedAsync(requestId, userId, ct) is { IsOk: false } denied) return Refusal(denied.Error!);
        return await RespondAsync(await service.SelectAsync(requestId, offerId, userId, ct), views, http, ct);
    }

    // T8
    public static async Task<IResult> NotAgreed(
        Guid requestId, Guid offerId, ClaimsPrincipal principal, TaxiRequestService service, TaxiRequestViews views, HttpContext http, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await service.OwnedAsync(requestId, userId, ct) is { IsOk: false } denied) return Refusal(denied.Error!);
        return await RespondAsync(await service.NotAgreedAsync(requestId, offerId, userId, ct), views, http, ct);
    }

    // T9
    public static async Task<IResult> End(
        Guid requestId, ClaimsPrincipal principal, TaxiRequestService service, TaxiBookingService bookings, TaxiRequestViews views,
        HttpContext http, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await service.OwnedAsync(requestId, userId, ct) is { IsOk: false } denied) return Refusal(denied.Error!);
        var ended = await bookings.EndByTravellerAsync(requestId, userId, ct);
        if (!ended.Ok) return ActionRefusal(ended);
        return await RespondAsync(await service.OwnedAsync(requestId, userId, ct), views, http, ct);
    }

    // T10
    public static async Task<IResult> Rate(
        Guid requestId, RateTaxiRequestDto body, IValidator<RateTaxiRequestDto> validator, ClaimsPrincipal principal,
        TaxiRequestService service, TaxiBookingService bookings, TaxiRequestViews views, HttpContext http, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await service.OwnedAsync(requestId, userId, ct) is { IsOk: false } denied) return Refusal(denied.Error!);
        if (await ValidateAsync(validator, body) is { } invalid) return invalid;
        var rated = await bookings.RateAsync(requestId, userId, body.Stars, ct);
        if (!rated.Ok) return ActionRefusal(rated);
        return await RespondAsync(await service.OwnedAsync(requestId, userId, ct), views, http, ct);
    }
}
