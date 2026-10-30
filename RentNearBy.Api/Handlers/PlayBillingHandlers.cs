// DI: none of its own (static handlers). Needs IPlayBillingSyncService and IPlayBillingClient registered
// (ServiceCollectionExtensions, see PlayBillingClient.cs / PlayEntitlementApplier.cs headers).
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// /api/v1/plans Google Play routes. The purchase token is verified only through IPlayBillingSyncService
// (subscriptionsv2.get); the client's productId is never trusted beyond matching a line item.
public static class PlayBillingHandlers
{
    private const int VerifyRateLimit = 20;
    private const int RestoreRateLimit = 30;
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(10);

    public static async Task<IResult> VerifyPlayPurchase(
        Guid id, VerifyPlayPurchaseRequest request, IValidator<VerifyPlayPurchaseRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPlanPurchaseService purchases, IPlayBillingSyncService sync,
        IPlayBillingClient client, IRateLimitService rateLimiter, IHubContext<NotificationHub> hubContext, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage, PlanErrorTypes.InvalidRequest);

        try
        {
            await RateLimitAsync(rateLimiter, $"plan:verify-play:{userId}", VerifyRateLimit);
            EnsureConfigured(client);

            var purchase = await unitOfWork.PlanPurchases.GetByIdAsync(id);
            if (purchase == null || purchase.UserId != userId)
                throw PlanServiceException.NotFound(PlanErrorTypes.PurchaseNotFound, "Purchase not found.");
            if (purchase.Provider != PaymentProviders.Play)
                throw PlanServiceException.Conflict(PlanErrorTypes.ProviderMismatch, "This purchase is not a Google Play purchase.");

            var result = await SyncAsync(sync, new PlaySyncRequest(request.PurchaseToken, PurchaseSources.VerifyPlay,
                CallerUserId: userId, PurchaseId: id, ClientProductId: request.ProductId));

            if (result.Outcome == PlaySyncOutcome.ApiError)
                throw ApiErrorOf(result.ApiError);
            if (result.Outcome == PlaySyncOutcome.Invalid)
                throw PlanServiceException.BadRequest(PlanErrorTypes.PlayTokenInvalid, "This Google Play purchase could not be verified.");
            if (result.Outcome == PlaySyncOutcome.Rejected && result.BlockedReason == GrantBlockedReasons.AccountMismatch)
                throw PlanServiceException.Forbidden(PlanErrorTypes.PurchaseBelongsToAnotherAccount,
                    "This Google Play subscription belongs to another account.");

            await PushAsync(hubContext, result.Notification, userId);

            var statusId = result.PurchaseId is { } chargeId && chargeId != id
                && await unitOfWork.PlanPurchases.GetByIdAsync(chargeId) is { } charge && charge.UserId == userId
                ? chargeId : id;
            var status = await purchases.GetStatusAsync(userId, statusId);
            var dto = new VerifyPlayResultDto
            {
                PurchaseId = status.PurchaseId,
                Status = status.Status,
                PlanName = status.PlanName,
                GrantedDays = status.GrantedDays,
                EndsAt = status.EndsAt ?? (result.Outcome == PlaySyncOutcome.Detached ? result.ExpiryTime : null),
                FailureReason = status.FailureReason,
                GrantBlockedReason = status.GrantBlockedReason ?? result.BlockedReason,
                Provider = PaymentProviders.Play,
                Outcome = VerifyOutcomeOf(result.Outcome),
            };
            return OkResponse(dto);
        }
        catch (PlanServiceException ex)
        {
            return PlanErrorResponse(ex, httpContext);
        }
    }

    public static async Task<IResult> RestorePlayPurchases(
        RestorePlayPurchasesRequest request, IValidator<RestorePlayPurchasesRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPlanPurchaseService purchases, IPlayBillingSyncService sync,
        IPlayBillingClient client, IRateLimitService rateLimiter, IHubContext<NotificationHub> hubContext, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage, PlanErrorTypes.InvalidRequest);

        try
        {
            if (request.Purchases.Count > PlayRules.RestoreMaxTokens)
                throw PlanServiceException.BadRequest(PlanErrorTypes.RestoreLimit, $"Restore at most {PlayRules.RestoreMaxTokens} purchases at once.");
            await RateLimitAsync(rateLimiter, $"plan:restore:{userId}", RestoreRateLimit);
            EnsureConfigured(client);

            var results = new List<RestorePlayItemResultDto>(request.Purchases.Count);
            var byToken = new Dictionary<string, RestorePlayItemResultDto>(StringComparer.Ordinal);
            var transientFailures = 0;
            for (var index = 0; index < request.Purchases.Count; index++)
            {
                var item = request.Purchases[index];
                if (byToken.TryGetValue(item.PurchaseToken, out var seen))
                {
                    results.Add(new RestorePlayItemResultDto
                    {
                        Index = index,
                        PurchaseToken = item.PurchaseToken,
                        Outcome = seen.Outcome,
                        PurchaseId = seen.PurchaseId,
                        Status = seen.Status,
                        EndsAt = seen.EndsAt,
                    });
                    continue;
                }

                PlaySyncResult result;
                try
                {
                    result = await SyncAsync(sync, new PlaySyncRequest(item.PurchaseToken, PurchaseSources.Restore,
                        CallerUserId: userId, ClientProductId: item.ProductId));
                }
                catch (PlanServiceException ex) when (ex.StatusCode == 503)
                {
                    result = new PlaySyncResult(PlaySyncOutcome.ApiError, null, null, null, null, null, null,
                        PlayApiErrorKind.Transient, null, null);
                }
                catch (Exception ex) when (ex is not PlanServiceException)
                {
                    result = new PlaySyncResult(PlaySyncOutcome.ApiError, null, null, null, null, null, null,
                        PlayApiErrorKind.Transient, null, null);
                }
                if (result.Outcome == PlaySyncOutcome.ApiError && result.ApiError is not (PlayApiErrorKind.NotFound or PlayApiErrorKind.Invalid))
                    transientFailures++;

                await PushAsync(hubContext, result.Notification, userId);

                string? purchaseStatus = null;
                if (result.PurchaseId is { } purchaseId
                    && await unitOfWork.PlanPurchases.GetByIdAsync(purchaseId) is { } row && row.UserId == userId)
                    purchaseStatus = row.Status;

                var dto = new RestorePlayItemResultDto
                {
                    Index = index,
                    PurchaseToken = item.PurchaseToken,
                    Outcome = RestoreOutcomeOf(result),
                    PurchaseId = purchaseStatus != null ? result.PurchaseId : null,
                    Status = purchaseStatus,
                    EndsAt = result.ExpiryTime,
                };
                byToken[item.PurchaseToken] = dto;
                results.Add(dto);
            }

            // Every token failed on Google's side: the app retries the whole restore later.
            if (transientFailures == byToken.Count)
                throw PlanServiceException.Gateway("Google Play is not reachable. Please try again.");

            return OkResponse(new RestorePlayResultDto { Results = results, Me = await purchases.GetMyPlanAsync(userId) });
        }
        catch (PlanServiceException ex)
        {
            return PlanErrorResponse(ex, httpContext);
        }
    }

    private static async Task<PlaySyncResult> SyncAsync(IPlayBillingSyncService sync, PlaySyncRequest request)
    {
        try
        {
            return await sync.SyncTokenAsync(request);
        }
        catch (PlayApiException ex)
        {
            return new PlaySyncResult(PlaySyncOutcome.ApiError, null, null, null, null, null, null, ex.Kind, null, null);
        }
    }

    // Detached is the extra outcome of R-DETACHED (closed subscription; EndsAt carries Google's expiry).
    private static string VerifyOutcomeOf(PlaySyncOutcome outcome) => outcome switch
    {
        PlaySyncOutcome.Granted or PlaySyncOutcome.Renewed or PlaySyncOutcome.Switched or PlaySyncOutcome.Recovered => "Granted",
        PlaySyncOutcome.Pending => "Pending",
        PlaySyncOutcome.Detached => "Detached",
        PlaySyncOutcome.Unchanged or PlaySyncOutcome.Grace or PlaySyncOutcome.CarriedOver => "AlreadyProcessed",
        _ => "Rejected",
    };

    private static string RestoreOutcomeOf(PlaySyncResult result) => result.Outcome switch
    {
        PlaySyncOutcome.Granted or PlaySyncOutcome.Renewed or PlaySyncOutcome.Switched or PlaySyncOutcome.Recovered => "Granted",
        PlaySyncOutcome.Unchanged or PlaySyncOutcome.Grace or PlaySyncOutcome.OnHold or PlaySyncOutcome.Paused
            or PlaySyncOutcome.CarriedOver => "Unchanged",
        PlaySyncOutcome.Pending => "Pending",
        PlaySyncOutcome.PendingCanceled or PlaySyncOutcome.Cancelled or PlaySyncOutcome.Expired or PlaySyncOutcome.Revoked
            or PlaySyncOutcome.Replaced => "Expired",
        PlaySyncOutcome.Rejected when result.BlockedReason == GrantBlockedReasons.AccountMismatch => "OtherAccount",
        PlaySyncOutcome.Rejected when result.BlockedReason == GrantBlockedReasons.ProductUnknown => "ProductUnknown",
        PlaySyncOutcome.ProductUnknown => "ProductUnknown",
        PlaySyncOutcome.Rejected => "Rejected",
        PlaySyncOutcome.Detached => "Detached",
        _ => "Error",
    };

    private static PlanServiceException ApiErrorOf(PlayApiErrorKind? kind) => kind switch
    {
        PlayApiErrorKind.NotFound or PlayApiErrorKind.Invalid =>
            PlanServiceException.BadRequest(PlanErrorTypes.PlayTokenInvalid, "This Google Play purchase could not be verified."),
        _ => PlanServiceException.Gateway("Checking with Google Play. Please try again."),
    };

    private static void EnsureConfigured(IPlayBillingClient client)
    {
        if (!client.IsConfigured)
            throw new PlanServiceException(503, PlanErrorTypes.PlayNotConfigured, "Google Play billing is not available right now.");
    }

    private static async Task RateLimitAsync(IRateLimitService rateLimiter, string key, int max)
    {
        var rl = await rateLimiter.CheckAsync(key, max, RateWindow);
        if (!rl.IsAllowed) throw PlanServiceException.TooMany(rl.RetryAfter);
    }

    private static async Task PushAsync(IHubContext<NotificationHub> hubContext, NotificationEvent? notification, Guid userId)
    {
        if (notification != null && notification.TargetUserId == userId)
            await AdminUserPlanHandlers.PushNotificationAsync(hubContext, notification);
    }
}
