using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be3;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Buddy.Be3;

// E1 to E8 and E17 (contract 2.3). Thin: caller, rate limit, validator, one service call, envelope.
public static class TripExpenseHandlers
{
    // E1
    public static async Task<IResult> List(
        Guid tripId, string? cursor, int? limit, ClaimsPrincipal principal, TripExpenseReader reader, IRateLimitService limiter,
        HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.ExpenseRead(me)) is { } limited) return limited;
        if (!BuddyHttp.TryExpenseCursor(cursor, out var key)) return BuddyHttp.BadCursor();
        var take = BuddyHttp.ClampLimit(limit, BuddyLimits.ExpensesPageDefault, BuddyLimits.ExpensesPageMax);
        return Answer(await reader.ListAsync(tripId, me, key, take, ct));
    }

    // E2
    public static async Task<IResult> Save(
        Guid tripId, Guid expenseId, SaveExpenseRequestDto body, IValidator<SaveExpenseRequestDto> validator, ClaimsPrincipal principal,
        TripExpenseService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (expenseId == Guid.Empty) return BuddyHttp.Error(BuddyErrorTypes.BadRequest);
        return await BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.ExpenseWrite(me), async () =>
        {
            if (await InvalidAsync(validator, body) is { } invalid) return invalid;
            var result = await service.SaveAsync(tripId, expenseId, me, body, ct);
            if (result.IsOk && result.Created)
                return CreatedResponse(result.Value, $"/api/v1/trips/{tripId}/expenses/{expenseId}");
            return Answer(result);
        });
    }

    // E3
    public static async Task<IResult> Delete(
        Guid tripId, Guid expenseId, [FromQuery] int baseVersion, [FromQuery] Guid opId, [FromQuery] bool? paymentsAcknowledged,
        ClaimsPrincipal principal, TripExpenseService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (opId == Guid.Empty || baseVersion < 1) return BuddyHttp.Error(BuddyErrorTypes.BadRequest);
        return await BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.ExpenseWrite(me),
            async () => Answer(await service.DeleteAsync(tripId, expenseId, me, baseVersion, opId, paymentsAcknowledged ?? false, ct)));
    }

    // E4
    public static async Task<IResult> Detail(
        Guid tripId, Guid expenseId, ClaimsPrincipal principal, TripExpenseReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.ExpenseRead(me)) is { } limited) return limited;
        return Answer(await reader.DetailAsync(tripId, expenseId, me, ct));
    }

    // E5
    public static async Task<IResult> SkipSuggestion(
        Guid tripId, SkipSuggestionRequestDto body, IValidator<SkipSuggestionRequestDto> validator, ClaimsPrincipal principal,
        TripExpenseService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        return await BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.ExpenseWrite(me), async () =>
        {
            if (await InvalidAsync(validator, body) is { } invalid) return invalid;
            return NoContent(await service.SkipSuggestionAsync(tripId, me, body.SourceId, ct));
        });
    }

    // E6
    public static async Task<IResult> KeepLateJoiner(
        Guid tripId, Guid userId, ClaimsPrincipal principal, TripExpenseService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        return await BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.ExpenseWrite(me),
            async () => NoContent(await service.KeepAsync(tripId, me, userId, ct)));
    }

    // E7
    public static async Task<IResult> ShareWith(
        Guid tripId, ShareWithRequestDto body, IValidator<ShareWithRequestDto> validator, ClaimsPrincipal principal,
        TripExpenseService service, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        return await BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.ExpenseWrite(me), async () =>
        {
            if (await InvalidAsync(validator, body) is { } invalid) return invalid;
            return Answer(await service.ShareWithAsync(tripId, me, body, ct));
        });
    }

    // E8
    public static async Task<IResult> Balances(
        Guid tripId, ClaimsPrincipal principal, TripBalanceReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.ExpenseRead(me)) is { } limited) return limited;
        return Answer(await reader.BalancesAsync(tripId, me, ct));
    }

    // E17
    public static async Task<IResult> Summary(
        Guid tripId, ClaimsPrincipal principal, TripBalanceReader reader, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.ExpenseRead(me)) is { } limited) return limited;
        return Answer(await reader.SummaryAsync(tripId, me, ct));
    }

    private static IResult Answer<T>(ExpenseResult<T> result)
    {
        if (result.IsOk) return OkResponse(result.Value);
        return result.Error == BuddyErrorTypes.TripNotFound ? BuddyHttp.TripNotFound() : BuddyHttp.Error(result.Error!, result.ErrorData);
    }

    private static IResult NoContent(ExpenseResult<bool> result)
        => result.IsOk ? NoContentResponse() : Answer(result);

    private static async Task<IResult?> InvalidAsync<T>(IValidator<T> validator, T body)
    {
        var validation = await validator.ValidateAsync(body);
        if (validation.IsValid) return null;
        var first = validation.Errors[0];
        var typed = !string.IsNullOrEmpty(first.ErrorCode) && first.ErrorCode != BuddyErrorTypes.BadRequest && IsBuddyType(first.ErrorCode);
        return typed ? BuddyHttp.Error(first.ErrorCode) : BuddyHttp.Invalid(validation);
    }

    private static bool IsBuddyType(string code) => BuddyErrorTypes.MessageOf(code) != BuddyErrorTypes.MessageOf(string.Empty);
}
