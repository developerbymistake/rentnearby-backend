using System.Security.Claims;
using FluentValidation;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Buddy.Be4;

// E9, E10, E12, E18, E19. Limit exp:pay 10 per minute; a refused call gives its permit back.
public static class TripPaymentHandlers
{
    public static async Task<IResult> SaidPaid(
        Guid tripId, SaidPaidRequestDto body, IValidator<SaidPaidRequestDto> validator, ClaimsPrincipal principal,
        TripPaymentService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var userId)) return UnauthorizedResponse();
        return await BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.ExpensePay(userId), async () =>
        {
            var validation = await validator.ValidateAsync(body, ct);
            if (!validation.IsValid) return BuddyHttp.Invalid(validation);
            var result = await service.SaidPaidAsync(tripId, userId, body, ct);
            if (result.Error is not null) return Error(result);
            var dto = Dto(result);
            return result.Created ? CreatedResponse(dto, $"/api/v1/trips/{tripId}/balances") : OkResponse(dto);
        });
    }

    public static Task<IResult> Received(
        Guid tripId, Guid paymentId, ClaimsPrincipal principal, TripPaymentService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => TransitionAsync(principal, limiter, http, userId => service.ReceivedAsync(tripId, userId, paymentId, ct));

    public static Task<IResult> Withdraw(
        Guid tripId, Guid paymentId, ClaimsPrincipal principal, TripPaymentService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => TransitionAsync(principal, limiter, http, userId => service.WithdrawAsync(tripId, userId, paymentId, ct));

    public static Task<IResult> NotReceived(
        Guid tripId, Guid paymentId, ClaimsPrincipal principal, TripPaymentService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => TransitionAsync(principal, limiter, http, userId => service.NotReceivedAsync(tripId, userId, paymentId, ct));

    public static async Task<IResult> Remind(
        Guid tripId, RemindRequestDto body, IValidator<RemindRequestDto> validator, ClaimsPrincipal principal,
        TripPaymentService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var userId)) return UnauthorizedResponse();
        return await BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.ExpensePay(userId), async () =>
        {
            var validation = await validator.ValidateAsync(body, ct);
            if (!validation.IsValid) return BuddyHttp.Invalid(validation);
            var error = await service.RemindAsync(tripId, userId, body.FromUserId, ct);
            if (error == BuddyErrorTypes.TripNotFound) return BuddyHttp.TripNotFound();
            return error is null ? NoContentResponse() : BuddyHttp.Error(error);
        });
    }

    private static async Task<IResult> TransitionAsync(
        ClaimsPrincipal principal, IRateLimitService limiter, HttpContext http, Func<Guid, Task<PaymentResult>> run)
    {
        if (!BuddyHttp.TryCaller(principal, out var userId)) return UnauthorizedResponse();
        return await BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.ExpensePay(userId), async () =>
        {
            var result = await run(userId);
            return result.Error is not null ? Error(result) : OkResponse(Dto(result));
        });
    }

    private static PaymentWriteResponseDto Dto(PaymentResult r) => new() { Payment = r.Payment, BalancesVersion = r.BalancesVersion };

    private static IResult Error(PaymentResult r)
        => r.Error == BuddyErrorTypes.TripNotFound ? BuddyHttp.TripNotFound() : BuddyHttp.Error(r.Error!, r.ErrorData);
}
