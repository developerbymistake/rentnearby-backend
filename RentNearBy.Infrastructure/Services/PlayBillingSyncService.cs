using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

public static class PlaySyncLogEvents
{
    public static readonly EventId Sync = new(7101, "play.sync");
    public static readonly EventId RtdnReceived = new(7102, "play.rtdn.received");
    public static readonly EventId RtdnAuthFailed = new(7103, "play.rtdn.auth_failed");
    public static readonly EventId RtdnUnmapped = new(7104, "play.rtdn.unmapped");
    public static readonly EventId ApiError = new(7105, "play.api.error");
    public static readonly EventId AckOverdue = new(7106, "play.ack.overdue");
    public static readonly EventId ActionExecuted = new(7107, "play.action.executed");
    public static readonly EventId ActionFailed = new(7108, "play.action.failed");
    public static readonly EventId SweepStaleExpiry = new(7109, "play.sweep.stale_expiry");
    public static readonly EventId SweepDetachedExpiry = new(7110, "play.sweep.detached_expiry");
    public static readonly EventId ReplacementOldToken = new(7111, "play.replacement.old_token");
    public static readonly EventId DeferredUnknownProduct = new(7112, "play.deferred.unknown_product");
    public static readonly EventId VoidedApplied = new(7113, "play.voided.applied");
    public static readonly EventId ChargebackReview = new(7114, "play.chargeback.review");
    public static readonly EventId AdminTokenViewed = new(7115, "play.admin.token_viewed");
    public static readonly EventId AdminRefund = new(7116, "play.admin.refund");
    public static readonly EventId CarryOverApplied = new(7117, "play.carryover.applied");
    public static readonly EventId Reattach = new(7118, "play.reattach");

    public static string Tail(string? token)
        => string.IsNullOrEmpty(token) ? string.Empty
            : token.Length <= PlayRules.TokenTailLength ? token : token[^PlayRules.TokenTailLength..];
}

// Contract 3.1-3.5: snapshot first (outside any transaction), then owner lock -> token row lock -> re-read,
// then exactly one row of the 3.1 table. Money rows are never set Refunded on this path (R-MONEY).
public class PlayBillingSyncService(
    ApplicationDbContext db,
    IUnitOfWork unitOfWork,
    IPlaySubscriptionRepository subscriptions,
    IPlayBillingClient client,
    IPlayEntitlementApplier applier,
    IPlanPurchaseService purchases,
    IPlanCatalogCache catalog,
    ILiveSlotService slots,
    IListingCacheInvalidator cacheInvalidator,
    IRabbitMqPublisher publisher,
    IOptions<PlayBillingOptions> options,
    ILogger<PlayBillingSyncService> logger) : IPlayBillingSyncService
{
    private const int MaxApplyAttempts = 3;
    private const int MaxActionAttemptsBeforeAlarm = 20;
    private static readonly TimeSpan DueGrace = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan VoidedLookBack = TimeSpan.FromDays(30);
    private static readonly string[] NoActionStates = [PlayStates.Expired, PlayStates.PendingPurchaseCanceled];

    private enum ResolveKind { Detached, Rejected, User, None }

    private sealed record Resolution(ResolveKind Kind, Guid? UserId);

    private sealed record Mapping(
        PlaySubscriptionLineItem Primary,
        CatalogPlan? Plan,
        CatalogPrice? Price,
        CatalogPlan? ScheduledPlan,
        string PurchasedProductId);

    private sealed class Applied
    {
        public required PlaySyncResult Result { get; init; }
        public PlayApplyResult? Apply { get; init; }
        public List<NotificationEvent> Notifications { get; } = [];
        public (Guid UserId, string FeatureKey)? Bust { get; set; }
    }

    private PlayBillingOptions Options => options.Value;

    // ------------------------------------------------------------------ sync

    public async Task<PlaySyncResult> SyncTokenAsync(PlaySyncRequest request)
    {
        if (!client.IsConfigured) throw NotConfigured();
        var token = request.PurchaseToken;
        var started = Stopwatch.GetTimestamp();

        PlaySubscriptionSnapshot snapshot;
        try
        {
            snapshot = await client.GetSubscriptionAsync(token);
        }
        catch (PlayApiException ex)
        {
            logger.LogWarning(PlaySyncLogEvents.ApiError, "Play get failed ({Kind}) for token {TokenTail} from {Source}: {Message}",
                ex.Kind, PlaySyncLogEvents.Tail(token), request.Source, ex.Message);
            await db.PlaySubscriptions
                .Where(s => s.PurchaseToken == token)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSyncError, Truncate($"{ex.Kind}: {ex.Message}", 300)));
            return Result(PlaySyncOutcome.ApiError, apiError: ex.Kind);
        }

        Applied? applied = null;
        for (var attempt = 1; applied == null; attempt++)
        {
            applied = await TryApplyAsync(request, snapshot);
            if (applied == null && attempt >= MaxApplyAttempts)
                throw new InvalidOperationException("Play sync could not settle the subscription row; it is retried later.");
        }

        await AfterCommitAsync(applied);

        var result = applied.Result;
        logger.LogInformation(PlaySyncLogEvents.Sync,
            "Play sync token {TokenTail} source {Source} rtdn {RtdnType}: {Outcome} user {UserId} in {Ms} ms",
            PlaySyncLogEvents.Tail(token), request.Source, request.RtdnType, result.Outcome, result.UserId,
            (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    // Returns null when the row changed under us (insert race or 3.5 step 3b mismatch): the caller re-resolves.
    private async Task<Applied?> TryApplyAsync(PlaySyncRequest request, PlaySubscriptionSnapshot snapshot)
    {
        var token = request.PurchaseToken;
        var now = DateTime.UtcNow;
        var cat = await catalog.GetAsync();
        var existing = await db.PlaySubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.PurchaseToken == token);

        var mapping = Map(snapshot, cat, request.ClientProductId);
        if (mapping == null)
        {
            const string error = "Invalid snapshot: no line item carries an expiry time";
            logger.LogWarning(PlaySyncLogEvents.ApiError, "Play snapshot for token {TokenTail} is not usable ({State})",
                PlaySyncLogEvents.Tail(token), snapshot.State);
            if (existing != null)
                await db.PlaySubscriptions
                    .Where(s => s.Id == existing.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSyncError, error));
            return new Applied { Result = Result(PlaySyncOutcome.Invalid, existing?.Id, expiry: snapshot.ExpiryTime) };
        }

        var pre = await ResolveAsync(existing, snapshot, request);

        // Same Google account, another app account (F-15): the token stays with its owner, the caller gets nothing.
        if (pre.Kind == ResolveKind.User && request.CallerUserId is { } caller && pre.UserId != caller)
            return new Applied
            {
                Result = Result(PlaySyncOutcome.Rejected, existing?.Id, blocked: GrantBlockedReasons.AccountMismatch,
                    expiry: snapshot.ExpiryTime),
            };

        var reattach = pre.Kind == ResolveKind.Detached && existing != null && CanReattach(existing, snapshot, request, mapping);
        Guid? lockUser = pre.Kind == ResolveKind.User ? pre.UserId : reattach ? existing!.UserId : null;

        var tx = await db.Database.BeginTransactionAsync();
        try
        {
            if (lockUser != null) await slots.LockOwnerAsync(lockUser.Value);

            var inserted = false;
            if (existing == null)
            {
                db.PlaySubscriptions.Add(new PlaySubscription
                {
                    Id = Guid.NewGuid(),
                    PurchaseToken = token,
                    ProductId = mapping.Primary.ProductId,
                    PurchasedProductId = mapping.PurchasedProductId,
                    MappingStatus = PlayMappingStatuses.Unmapped,
                    State = snapshot.State,
                    AcknowledgementState = snapshot.AcknowledgementState,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                try
                {
                    await db.SaveChangesAsync();
                    inserted = true;
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex, "ix_playsubscriptions_token"))
                {
                    await tx.RollbackAsync();
                    db.ChangeTracker.Clear();
                    return null;
                }
            }

            await subscriptions.LockByTokenAsync(token);
            var sub = await subscriptions.GetByTokenAsync(token);
            if (sub == null
                || (!inserted && (sub.MappingStatus != existing!.MappingStatus || sub.UserId != existing.UserId
                    || sub.PendingServerAction != existing.PendingServerAction || sub.DetachedReason != existing.DetachedReason)))
            {
                await tx.RollbackAsync();
                db.ChangeTracker.Clear();
                return null;
            }

            var prevState = inserted ? null : sub.State;
            foreach (var entry in db.ChangeTracker.Entries<UserPlan>().Where(e => e.Entity.PlaySubscriptionId == sub.Id).ToList())
                entry.State = EntityState.Detached;
            var ownedPlan = await db.UserPlans
                .Where(u => u.PlaySubscriptionId == sub.Id)
                .OrderByDescending(u => u.Status == UserPlanStatuses.Active)
                .ThenByDescending(u => u.UpdatedAt)
                .FirstOrDefaultAsync();

            var applied = await DispatchAsync(request, snapshot, mapping, sub, pre, reattach, prevState, ownedPlan, cat, now);

            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return applied;
        }
        catch
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            await tx.DisposeAsync();
        }
    }

    // The 3.1 table, top to bottom; first match wins.
    private async Task<Applied> DispatchAsync(
        PlaySyncRequest request, PlaySubscriptionSnapshot snapshot, Mapping mapping, PlaySubscription sub,
        Resolution pre, bool reattach, string? prevState, UserPlan? ownedPlan, PlanCatalogSnapshot cat, DateTime now)
    {
        var token = request.PurchaseToken;

        // R-DETACHED / R-REATTACH
        if (sub.MappingStatus == PlayMappingStatuses.Detached)
        {
            if (reattach && sub.UserId is { } owner && mapping.Plan != null && mapping.Price != null
                && CanReattach(sub, snapshot, request, mapping)
                && await RoleFeatureAsync(owner) == mapping.Plan.FeatureKey
                && GuardPasses(snapshot, owner))
            {
                logger.LogInformation(PlaySyncLogEvents.Reattach, "Play token {TokenTail} re-attached to user {UserId}",
                    PlaySyncLogEvents.Tail(token), owner);
                sub.DetachedReason = null;
                return await ApplyEntitledAsync(request, snapshot, mapping, sub, owner, reattach: true, now);
            }
            RefreshOnly(sub, snapshot, request, now);
            return new Applied { Result = Result(PlaySyncOutcome.Detached, sub.Id, expiry: snapshot.ExpiryTime) };
        }

        if (sub.MappingStatus == PlayMappingStatuses.Rejected)
        {
            RefreshOnly(sub, snapshot, request, now);
            return mapping.Plan == null
                ? new Applied { Result = Result(PlaySyncOutcome.ProductUnknown, sub.Id, blocked: GrantBlockedReasons.ProductUnknown, expiry: snapshot.ExpiryTime) }
                : new Applied { Result = Result(PlaySyncOutcome.Rejected, sub.Id, blocked: GrantBlockedReasons.AccountMismatch, expiry: snapshot.ExpiryTime) };
        }

        // R-REPLACED: never expires, flips or pushes; the new token's sync (R-SWITCH) moves the row.
        if (snapshot.State == PlayStates.Expired && snapshot.CancelReason == PlayCancelReasons.Replacement)
        {
            RefreshOnly(sub, snapshot, request, now);
            sub.State = PlayStates.Expired;
            sub.MappingStatus = PlayMappingStatuses.Detached;
            sub.DetachedReason = PlayDetachReasons.Replaced;
            var successor = await subscriptions.GetMappedByLinkedTokenAsync(token);
            logger.LogInformation(PlaySyncLogEvents.ReplacementOldToken,
                "Play token {TokenTail} replaced (successor already mapped: {Landed})", PlaySyncLogEvents.Tail(token), successor != null);
            return new Applied
            {
                Result = Result(successor != null ? PlaySyncOutcome.Unchanged : PlaySyncOutcome.Replaced, sub.Id, expiry: snapshot.ExpiryTime),
            };
        }

        var userId = pre.Kind == ResolveKind.User ? pre.UserId : null;

        // R-REJECT-ACC
        if (userId != null && !GuardPasses(snapshot, userId.Value))
        {
            Upsert(sub, snapshot, request, mapping, now, compareFields: true);
            Reject(sub, "Purchase belongs to another account");
            var applied = new Applied
            {
                Result = Result(PlaySyncOutcome.Rejected, sub.Id, blocked: GrantBlockedReasons.AccountMismatch, expiry: snapshot.ExpiryTime),
            };
            var row = await BlockChargeAsync(request, snapshot, mapping, sub, userId.Value, null, GrantBlockedReasons.AccountMismatch, now);
            return row == null ? applied : new Applied { Result = applied.Result with { PurchaseId = row.Id } };
        }

        // R-REJECT-PROD
        if (mapping.Plan == null || mapping.Price == null)
        {
            Upsert(sub, snapshot, request, mapping, now, compareFields: true);
            Reject(sub, "Unknown product");
            logger.LogWarning("Play token {TokenTail} has no catalog product ({ProductId}); revoke queued",
                PlaySyncLogEvents.Tail(token), mapping.Primary.ProductId);
            return new Applied
            {
                Result = Result(PlaySyncOutcome.ProductUnknown, sub.Id, blocked: GrantBlockedReasons.ProductUnknown, expiry: snapshot.ExpiryTime),
            };
        }

        var roleFeature = userId == null ? null : await RoleFeatureAsync(userId.Value);

        // R-UNMAPPED (a user deleted between resolution and lock lands here too)
        if (userId == null || (roleFeature == null && !await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId)))
        {
            Upsert(sub, snapshot, request, mapping, now, compareFields: true);
            sub.MappingStatus = PlayMappingStatuses.Unmapped;
            sub.UserId = null;
            logger.LogInformation(PlaySyncLogEvents.RtdnUnmapped, "Play token {TokenTail} has no app account yet ({State})",
                PlaySyncLogEvents.Tail(token), snapshot.State);
            return new Applied { Result = Result(PlaySyncOutcome.Unmapped, sub.Id, expiry: snapshot.ExpiryTime) };
        }

        var user = userId.Value;
        await unitOfWork.Users.SetPlayAccountKeyIfNullAsync(user, Hmac(user));

        // R-ROLE
        if (snapshot.IsEntitled && roleFeature != mapping.Plan.FeatureKey)
        {
            Upsert(sub, snapshot, request, mapping, now, compareFields: true);
            sub.MappingStatus = PlayMappingStatuses.Mapped;
            sub.UserId = user;
            QueueAction(sub, PlayServerActions.RevokeFull, "Role mismatch");
            var row = await BlockChargeAsync(request, snapshot, mapping, sub, user, user, GrantBlockedReasons.RoleMismatch, now);
            return new Applied
            {
                Result = Result(PlaySyncOutcome.Rejected, sub.Id, row?.Id, userId: user, feature: mapping.Plan.FeatureKey,
                    blocked: GrantBlockedReasons.RoleMismatch, expiry: snapshot.ExpiryTime),
            };
        }

        if (snapshot.IsEntitled)
            return await ApplyEntitledAsync(request, snapshot, mapping, sub, user, reattach: false, now);

        Upsert(sub, snapshot, request, mapping, now, compareFields: true);
        sub.MappingStatus = PlayMappingStatuses.Mapped;
        sub.UserId = user;
        return snapshot.State switch
        {
            PlayStates.Pending => await PendingAsync(request, mapping, sub, user, now),
            PlayStates.PendingPurchaseCanceled => await PendingCanceledAsync(request, mapping, sub, user),
            PlayStates.OnHold or PlayStates.Paused => await HoldOrPauseAsync(snapshot, mapping, sub, user, prevState, ownedPlan, cat, now),
            PlayStates.Expired or PlayStates.Canceled => await EndedAsync(request, snapshot, mapping, sub, user, prevState, ownedPlan, cat, now),
            _ => new Applied { Result = Result(PlaySyncOutcome.Unchanged, sub.Id, userId: user, feature: mapping.Plan.FeatureKey, expiry: snapshot.ExpiryTime) },
        };
    }

    private async Task<Applied> ApplyEntitledAsync(
        PlaySyncRequest request, PlaySubscriptionSnapshot snapshot, Mapping mapping, PlaySubscription sub, Guid userId,
        bool reattach, DateTime now)
    {
        var plan = mapping.Plan!;
        var price = mapping.Price!;
        // State / LatestOrderId / ExpiryTime / AutoRenewEnabled keep their previous values for the applier's compares.
        Upsert(sub, snapshot, request, mapping, now, compareFields: false);
        sub.MappingStatus = PlayMappingStatuses.Mapped;
        sub.UserId = userId;
        await unitOfWork.Users.SetPlayAccountKeyIfNullAsync(userId, Hmac(userId));
        await db.SaveChangesAsync();

        var openPurchase = (mapping.ScheduledPlan is { } scheduled
                ? await FindOpenDeferredSwitchAsync(request, userId, scheduled)
                : null)
            ?? await FindOpenPurchaseAsync(request, userId, plan, price);
        var target = new PlayResolvedTarget(userId, plan.FeatureKey, plan, price, Hmac(userId), mapping.ScheduledPlan,
            sub.PurchasedProductId, reattach);
        var result = await applier.ApplyEntitledInTxAsync(sub, snapshot, target, openPurchase, request.Source);

        var current = db.PlaySubscriptions.Local.FirstOrDefault(s => s.Id == sub.Id)
            ?? await db.PlaySubscriptions.FirstAsync(s => s.Id == sub.Id);
        SetCompareFields(current, snapshot);
        current.MappingStatus = PlayMappingStatuses.Mapped;
        current.UserId = userId;
        if (reattach) current.DetachedReason = null;

        var outcome = result.Kind switch
        {
            PlayApplyKind.Granted or PlayApplyKind.Resubscribed or PlayApplyKind.Reattached => PlaySyncOutcome.Granted,
            PlayApplyKind.Renewed => PlaySyncOutcome.Renewed,
            PlayApplyKind.Switched => PlaySyncOutcome.Switched,
            PlayApplyKind.Recovered => PlaySyncOutcome.Recovered,
            _ => snapshot.State == PlayStates.InGracePeriod ? PlaySyncOutcome.Grace : PlaySyncOutcome.Unchanged,
        };
        return new Applied
        {
            Apply = result,
            Result = new PlaySyncResult(outcome, sub.Id, result.ChargeRow?.Id ?? openPurchase?.Id, result.UserPlan.Id, userId,
                plan.FeatureKey, null, null, result.Notification, snapshot.ExpiryTime),
        };
    }

    // R-PENDING: the open order (or a synthetic one) waits for Google; never time-expired (3.7 P6).
    private async Task<Applied> PendingAsync(PlaySyncRequest request, Mapping mapping, PlaySubscription sub, Guid userId, DateTime now)
    {
        var plan = mapping.Plan!;
        var price = mapping.Price!;
        var row = await FindOpenPurchaseAsync(request, userId, plan, price);
        if (row != null)
        {
            await purchases.MarkPlayPendingAsync(row.Id);
            row.PlaySubscriptionId = sub.Id;
        }
        else if (!await db.PlanPurchases.AnyAsync(p => p.UserId == userId && p.FeatureKey == plan.FeatureKey
                     && (p.Status == PurchaseStatuses.Created || p.Status == PurchaseStatuses.Pending)))
        {
            row = NewChargeRow(request, mapping, sub, userId, null, PurchaseStatuses.Pending, now);
            row.ExpiresAt = now.Add(PlayRules.PendingOrderTtl);
            db.PlanPurchases.Add(row);
        }
        return new Applied
        {
            Result = Result(PlaySyncOutcome.Pending, sub.Id, row?.Id, userId: userId, feature: plan.FeatureKey),
        };
    }

    // R-PENDING-CANCEL
    private async Task<Applied> PendingCanceledAsync(PlaySyncRequest request, Mapping mapping, PlaySubscription sub, Guid userId)
    {
        var plan = mapping.Plan!;
        var row = await FindOpenPurchaseAsync(request, userId, plan, mapping.Price!);
        if (row == null)
        {
            var open = await db.PlanPurchases.AsNoTracking()
                .Where(p => p.PlaySubscriptionId == sub.Id && p.Provider == PaymentProviders.Play
                    && (p.Status == PurchaseStatuses.Created || p.Status == PurchaseStatuses.Pending))
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync();
            if (open != null) row = await LockOpenPlayPurchaseAsync(open.Value, userId);
        }
        if (row != null)
        {
            row.Status = PurchaseStatuses.Failed;
            row.FailureReason = "Payment not completed";
            row.PlaySubscriptionId = sub.Id;
        }
        return new Applied
        {
            Result = Result(PlaySyncOutcome.PendingCanceled, sub.Id, row?.Id, userId: userId, feature: plan.FeatureKey),
        };
    }

    // R-HOLD / R-PAUSE / R-HOLD-IDEMP
    private async Task<Applied> HoldOrPauseAsync(
        PlaySubscriptionSnapshot snapshot, Mapping mapping, PlaySubscription sub, Guid userId, string? prevState,
        UserPlan? ownedPlan, PlanCatalogSnapshot cat, DateTime now)
    {
        var paused = snapshot.State == PlayStates.Paused;
        var feature = mapping.Plan!.FeatureKey;
        var applied = new Applied
        {
            Result = Result(paused ? PlaySyncOutcome.Paused : PlaySyncOutcome.OnHold, sub.Id, userId: userId, feature: feature,
                expiry: snapshot.ExpiryTime),
        };

        if (ownedPlan is { Status: UserPlanStatuses.Active })
        {
            var planName = cat.GetPlan(ownedPlan.PlanId)?.Name ?? mapping.Plan.Name;
            if (ownedPlan.CarryOverDays > 0)
            {
                var flip = await CarryOverFlipInTxAsync(ownedPlan, planName, now);
                var carried = new Applied
                {
                    Result = Result(PlaySyncOutcome.CarriedOver, sub.Id, userPlanId: ownedPlan.Id, userId: userId, feature: feature,
                        expiry: snapshot.ExpiryTime, notification: flip),
                };
                carried.Notifications.Add(flip);
                carried.Bust = (userId, ownedPlan.FeatureKey);
                return carried;
            }
            if (await ExpirePlayRowInTxAsync(ownedPlan, sub.Id, snapshot.State, now))
            {
                var n = paused
                    ? PlanNotifications.Paused(userId, planName, snapshot.PausedAutoResumeTime)
                    : PlanNotifications.OnHold(userId, planName);
                db.NotificationEvents.Add(n);
                var expired = new Applied
                {
                    Result = applied.Result with { UserPlanId = ownedPlan.Id, Notification = n },
                };
                expired.Notifications.Add(n);
                expired.Bust = (userId, ownedPlan.FeatureKey);
                return expired;
            }
            return applied;
        }

        if (prevState != snapshot.State)
        {
            var planName = cat.GetPlan(ownedPlan?.PlanId ?? mapping.Plan.Id)?.Name ?? mapping.Plan.Name;
            var n = paused
                ? PlanNotifications.Paused(userId, planName, snapshot.PausedAutoResumeTime)
                : PlanNotifications.OnHold(userId, planName);
            db.NotificationEvents.Add(n);
            var pushed = new Applied { Result = applied.Result with { Notification = n } };
            pushed.Notifications.Add(n);
            return pushed;
        }
        return applied;
    }

    // R-EXPIRED / R-CANCELED-PAST. Charge rows are untouched (R-MONEY).
    private async Task<Applied> EndedAsync(
        PlaySyncRequest request, PlaySubscriptionSnapshot snapshot, Mapping mapping, PlaySubscription sub, Guid userId,
        string? prevState, UserPlan? ownedPlan, PlanCatalogSnapshot cat, DateTime now)
    {
        var feature = mapping.Plan!.FeatureKey;
        var revoked = snapshot.CancelReason is PlayCancelReasons.Developer or PlayCancelReasons.System
            || request.RtdnType == PlayRtdnTypes.Revoked;
        var outcome = revoked ? PlaySyncOutcome.Revoked : PlaySyncOutcome.Expired;
        var applied = new Applied
        {
            Result = Result(outcome, sub.Id, userId: userId, feature: feature, expiry: snapshot.ExpiryTime),
        };

        if (ownedPlan is { Status: UserPlanStatuses.Active })
        {
            var planName = cat.GetPlan(ownedPlan.PlanId)?.Name ?? mapping.Plan.Name;
            NotificationEvent n;
            if (ownedPlan.CarryOverDays > 0)
            {
                n = await CarryOverFlipInTxAsync(ownedPlan, planName, now);
                outcome = PlaySyncOutcome.CarriedOver;
            }
            else
            {
                var ended = revoked
                    ? await CancelPlayRowInTxAsync(ownedPlan, sub.Id, snapshot.State, now)
                    : await ExpirePlayRowInTxAsync(ownedPlan, sub.Id, snapshot.State, now);
                if (!ended) return applied;
                n = PlanNotifications.Ended(userId, planName, await LatestChargeRefundedAsync(sub, snapshot));
                db.NotificationEvents.Add(n);
            }
            var done = new Applied
            {
                Result = applied.Result with { Outcome = outcome, UserPlanId = ownedPlan.Id, Notification = n },
            };
            done.Notifications.Add(n);
            done.Bust = (userId, ownedPlan.FeatureKey);
            return done;
        }

        // Hold/pause ended without recovery: the row is already Expired, only the push is owed (once).
        if (ownedPlan is { Status: UserPlanStatuses.Expired } && prevState is PlayStates.OnHold or PlayStates.Paused)
        {
            var planName = cat.GetPlan(ownedPlan.PlanId)?.Name ?? mapping.Plan.Name;
            var n = PlanNotifications.Ended(userId, planName, await LatestChargeRefundedAsync(sub, snapshot));
            db.NotificationEvents.Add(n);
            var pushed = new Applied { Result = applied.Result with { UserPlanId = ownedPlan.Id, Notification = n } };
            pushed.Notifications.Add(n);
            return pushed;
        }
        return applied;
    }

    // ------------------------------------------------------------------ row primitives (caller holds tx + owner lock)

    private async Task<bool> ExpirePlayRowInTxAsync(UserPlan row, Guid playSubscriptionId, string playState, DateTime now)
    {
        var claimed = await db.UserPlans
            .Where(u => u.Id == row.Id && u.PlaySubscriptionId == playSubscriptionId && u.Status == UserPlanStatuses.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Status, UserPlanStatuses.Expired)
                .SetProperty(u => u.PlayState, playState)
                .SetProperty(u => u.UpdatedAt, now));
        db.Entry(row).State = EntityState.Detached;
        if (claimed == 0) return false;
        await slots.ExpireEffectsAsync(row.UserId, row.FeatureKey);
        return true;
    }

    private async Task<bool> CancelPlayRowInTxAsync(UserPlan row, Guid playSubscriptionId, string playState, DateTime now)
    {
        var claimed = await db.UserPlans
            .Where(u => u.Id == row.Id && u.PlaySubscriptionId == playSubscriptionId && u.Status == UserPlanStatuses.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Status, UserPlanStatuses.Cancelled)
                .SetProperty(u => u.EndsAt, u => u.StartsAt < now ? now : u.StartsAt.AddSeconds(1))
                .SetProperty(u => u.PlayState, playState)
                .SetProperty(u => u.Note, "Subscription revoked")
                .SetProperty(u => u.UpdatedAt, now));
        db.Entry(row).State = EntityState.Detached;
        if (claimed == 0) return false;
        await slots.ExpireEffectsAsync(row.UserId, row.FeatureKey);
        return true;
    }

    // D-09: the Play access stopped but carried-over days remain; the row becomes an AdminGrant row for them.
    private async Task<NotificationEvent> CarryOverFlipInTxAsync(UserPlan row, string planName, DateTime now)
    {
        var days = Math.Min(row.CarryOverDays, PlayRules.MaxCarryOverDays);
        var endsAt = now.AddDays(days);
        var grew = endsAt > row.EndsAt;
        row.Source = UserPlanSources.AdminGrant;
        row.Note = "Carry-over";
        row.PlaySubscriptionId = null;
        row.PlayState = null;
        row.PlayAutoRenewEnabled = null;
        row.PlayScheduledPlanId = null;
        row.EndsAt = endsAt;
        row.CarryOverDays = 0;
        row.ReminderSentAt = null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync();
        if (grew) await slots.ExtendValidUntilAsync(row.UserId, row.FeatureKey, endsAt);

        logger.LogInformation(PlaySyncLogEvents.CarryOverApplied, "Plan {UserPlanId} of user {UserId} moved to {Days} carry-over days",
            row.Id, row.UserId, days);
        var n = PlanNotifications.CarryOverActivated(row.UserId, planName, days, endsAt);
        db.NotificationEvents.Add(n);
        return n;
    }

    private async Task<bool> LatestChargeRefundedAsync(PlaySubscription sub, PlaySubscriptionSnapshot snapshot)
    {
        var orderId = snapshot.Entitled?.LatestOrderId ?? sub.LatestOrderId;
        if (orderId == null) return false;
        return await db.PlanPurchases.AsNoTracking()
            .AnyAsync(p => p.PlayOrderId == orderId && p.Status == PurchaseStatuses.Refunded);
    }

    // R-REJECT-ACC / R-ROLE: the charge row (existing, the caller's open order, or synthetic) -> Paid + reason.
    private async Task<PlanPurchase?> BlockChargeAsync(
        PlaySyncRequest request, PlaySubscriptionSnapshot snapshot, Mapping mapping, PlaySubscription sub,
        Guid resolvedUserId, Guid? rowUserId, string reason, DateTime now)
    {
        var orderId = snapshot.Entitled?.LatestOrderId;
        PlanPurchase? row = null;
        if (orderId != null)
        {
            var id = await db.PlanPurchases.AsNoTracking().Where(p => p.PlayOrderId == orderId).Select(p => (Guid?)p.Id).FirstOrDefaultAsync();
            if (id != null) row = await LockPurchaseAsync(id.Value);
        }
        if (row == null && request.PurchaseId is { } pid)
            row = await LockOpenPlayPurchaseAsync(pid, resolvedUserId);
        if (row == null)
        {
            if (mapping.Plan == null || mapping.Price == null) return null;
            row = NewChargeRow(request, mapping, sub, rowUserId, orderId, PurchaseStatuses.Paid, now);
            db.PlanPurchases.Add(row);
        }
        if (row.Status is PurchaseStatuses.Granted or PurchaseStatuses.Refunded) return row;

        row.Status = PurchaseStatuses.Paid;
        row.GrantBlockedReason = reason;
        row.PaidAt ??= now;
        row.PlayOrderId ??= orderId;
        row.PlaySubscriptionId = sub.Id;
        row.PlayProductId ??= mapping.Primary.ProductId;
        row.PlayBasePlanId ??= mapping.Primary.BasePlanId;
        row.PlayOfferId ??= mapping.Primary.OfferId;
        return row;
    }

    private PlanPurchase NewChargeRow(
        PlaySyncRequest request, Mapping mapping, PlaySubscription sub, Guid? userId, string? orderId, string status, DateTime now)
    {
        var plan = mapping.Plan!;
        var price = mapping.Price!;
        var trial = price.PlayTrialOfferId != null && mapping.Primary.OfferId == price.PlayTrialOfferId;
        var payable = trial ? 0
            : mapping.Primary.RecurringPriceMicros is { } micros && mapping.Primary.RecurringPriceCurrency == "INR"
                ? (int)Math.Round(micros / 1_000_000m, MidpointRounding.AwayFromZero)
                : price.PriceInr;
        return new PlanPurchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FeatureKey = plan.FeatureKey,
            PlanId = plan.Id,
            PlanNameSnapshot = plan.Name,
            CycleMonths = price.CycleMonths,
            GrantedDays = PlanCycles.DaysOf(price.CycleMonths),
            Intent = PurchaseIntents.New,
            Provider = PaymentProviders.Play,
            Currency = "INR",
            ListPriceInr = price.PriceInr,
            DiscountInr = trial ? price.PriceInr : 0,
            CreditInr = 0,
            PayableInr = payable,
            Status = status,
            CreatedAt = now,
            ExpiresAt = now,
            PaidAt = status == PurchaseStatuses.Paid ? now : null,
            PlaySubscriptionId = sub.Id,
            PlayOrderId = orderId,
            PlayProductId = mapping.Primary.ProductId,
            PlayBasePlanId = mapping.Primary.BasePlanId,
            PlayOfferId = mapping.Primary.OfferId,
            PlayPurchaseSource = PurchaseSourceTag(request.Source),
        };
    }

    // The caller's open Play order of the same plan+cycle, locked and tracked (3.5 step 4).
    private async Task<PlanPurchase?> FindOpenPurchaseAsync(PlaySyncRequest request, Guid userId, CatalogPlan plan, CatalogPrice price)
    {
        if (request.PurchaseId is { } pid)
        {
            var row = await LockOpenPlayPurchaseAsync(pid, userId);
            if (row != null && row.PlanId == plan.Id && row.CycleMonths == price.CycleMonths) return row;
        }
        var open = await unitOfWork.PlanPurchases.GetOpenPlayByUserFeatureAsync(userId, plan.FeatureKey, plan.Id, price.CycleMonths);
        return open == null ? null : await LockOpenPlayPurchaseAsync(open.Id, userId);
    }

    // A deferred downgrade keeps the OLD product entitled; the user's open order is the Switch to the scheduled plan.
    private async Task<PlanPurchase?> FindOpenDeferredSwitchAsync(PlaySyncRequest request, Guid userId, CatalogPlan scheduled)
    {
        if (request.PurchaseId is { } pid)
        {
            var row = await LockOpenPlayPurchaseAsync(pid, userId);
            if (row is { Intent: PurchaseIntents.Switch, PlayOrderId: null } && row.PlanId == scheduled.Id) return row;
        }
        var open = await db.PlanPurchases.AsNoTracking()
            .Where(p => p.UserId == userId && p.FeatureKey == scheduled.FeatureKey && p.PlanId == scheduled.Id
                && p.Provider == PaymentProviders.Play && p.Intent == PurchaseIntents.Switch && p.PlayOrderId == null
                && (p.Status == PurchaseStatuses.Created || p.Status == PurchaseStatuses.Pending))
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync();
        return open == null ? null : await LockOpenPlayPurchaseAsync(open.Value, userId);
    }

    private async Task<PlanPurchase?> LockOpenPlayPurchaseAsync(Guid purchaseId, Guid userId)
    {
        var row = await LockPurchaseAsync(purchaseId);
        return row != null && row.UserId == userId && row.Provider == PaymentProviders.Play
               && row.Status is PurchaseStatuses.Created or PurchaseStatuses.Pending
            ? row
            : null;
    }

    private async Task<PlanPurchase?> LockPurchaseAsync(Guid purchaseId)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"PlanPurchases\" WHERE \"Id\" = {purchaseId} FOR UPDATE");
        foreach (var entry in db.ChangeTracker.Entries<PlanPurchase>().Where(e => e.Entity.Id == purchaseId).ToList())
            entry.State = EntityState.Detached;
        return await db.PlanPurchases.FirstOrDefaultAsync(p => p.Id == purchaseId);
    }

    // ------------------------------------------------------------------ mapping / resolution

    // R-LINE: the entitled item drives everything; a pending/ended token without one falls back to the
    // client's product or the item that has no expiry yet. Null = nothing usable (outcome Invalid).
    private Mapping? Map(PlaySubscriptionSnapshot snapshot, PlanCatalogSnapshot cat, string? clientProductId)
    {
        var entitled = snapshot.Entitled;
        if (entitled == null && snapshot.State is PlayStates.Active or PlayStates.InGracePeriod or PlayStates.Canceled
                or PlayStates.OnHold or PlayStates.Paused)
            return null;
        var clientItem = clientProductId == null ? null : snapshot.Find(clientProductId);
        var primary = entitled ?? clientItem ?? snapshot.DeferredTarget;
        if (primary == null) return null;

        var mappedItem = cat.FindByPlayProduct(primary.ProductId) != null
            ? primary
            : snapshot.LineItems.FirstOrDefault(l => cat.FindByPlayProduct(l.ProductId) != null);
        var plan = mappedItem == null ? null : cat.FindByPlayProduct(mappedItem.ProductId);
        CatalogPrice? price = null;
        if (plan != null && mappedItem != null)
        {
            price = mappedItem.BasePlanId == null ? null : cat.FindPrice(plan.Id, mappedItem.BasePlanId);
            if (price == null)
            {
                price = plan.Prices.OrderBy(p => p.CycleMonths).FirstOrDefault();
                if (price != null)
                    logger.LogWarning("Play base plan {BasePlanId} of product {ProductId} is not in the catalog; using the {Cycle}-month price",
                        mappedItem.BasePlanId, mappedItem.ProductId, price.CycleMonths);
            }
        }

        CatalogPlan? scheduled = null;
        var deferred = entitled == null ? null : snapshot.DeferredProductId;
        if (deferred != null && deferred != entitled!.ProductId)
        {
            scheduled = cat.FindByPlayProduct(deferred);
            if (scheduled == null)
                logger.LogWarning(PlaySyncLogEvents.DeferredUnknownProduct, "Deferred Play product {ProductId} is not in the catalog", deferred);
        }

        var purchased = clientItem?.ProductId ?? snapshot.DeferredTarget?.ProductId ?? primary.ProductId;
        return new Mapping(primary, plan, price, scheduled, purchased);
    }

    // Contract 3.3, steps 0-6.
    private async Task<Resolution> ResolveAsync(PlaySubscription? existing, PlaySubscriptionSnapshot snapshot, PlaySyncRequest request)
    {
        if (existing?.MappingStatus == PlayMappingStatuses.Detached) return new Resolution(ResolveKind.Detached, null);
        if (existing?.MappingStatus == PlayMappingStatuses.Rejected) return new Resolution(ResolveKind.Rejected, null);
        if (existing is { MappingStatus: PlayMappingStatuses.Mapped, UserId: { } mapped })
            return new Resolution(ResolveKind.User, mapped);

        foreach (var linked in new[] { snapshot.LinkedPurchaseToken, snapshot.ExpiredPurchaseToken ?? existing?.ExpiredPurchaseToken })
        {
            if (string.IsNullOrEmpty(linked)) continue;
            var owner = await db.PlaySubscriptions.AsNoTracking()
                .Where(s => s.PurchaseToken == linked && s.UserId != null)
                .Select(s => s.UserId)
                .FirstOrDefaultAsync();
            if (owner != null) return new Resolution(ResolveKind.User, owner);
        }

        if (!string.IsNullOrEmpty(snapshot.ObfuscatedAccountId))
        {
            var byKey = await unitOfWork.Users.GetIdByPlayAccountKeyAsync(snapshot.ObfuscatedAccountId);
            if (byKey != null) return new Resolution(ResolveKind.User, byKey);
        }

        if (request.PurchaseId is { } purchaseId)
        {
            var owner = await db.PlanPurchases.AsNoTracking()
                .Where(p => p.Id == purchaseId)
                .Select(p => p.UserId)
                .FirstOrDefaultAsync();
            if (owner != null) return new Resolution(ResolveKind.User, owner);
        }
        if (request.CallerUserId is { } callerId) return new Resolution(ResolveKind.User, callerId);
        return new Resolution(ResolveKind.None, null);
    }

    // R-REATTACH preconditions that do not need the role (re-checked under the locks with the role).
    private static bool CanReattach(PlaySubscription sub, PlaySubscriptionSnapshot snapshot, PlaySyncRequest request, Mapping mapping)
        => sub.MappingStatus == PlayMappingStatuses.Detached
           && sub.DetachedReason == PlayDetachReasons.RoleChanged
           && sub.PendingServerAction == null
           && request.Source is PurchaseSources.Restore or PurchaseSources.VerifyPlay
           && sub.UserId != null && sub.UserId == request.CallerUserId
           && mapping.Plan != null && mapping.Price != null
           && snapshot.IsEntitled;

    private async Task<string?> RoleFeatureAsync(Guid userId)
    {
        var roleId = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (Guid?)u.RoleId).FirstOrDefaultAsync();
        return roleId == null ? null : UserRoles.ListingKindOf(roleId.Value);
    }

    // D-11: a token that names an obfuscated account must name this user's.
    private bool GuardPasses(PlaySubscriptionSnapshot snapshot, Guid userId)
        => string.IsNullOrEmpty(snapshot.ObfuscatedAccountId) || snapshot.ObfuscatedAccountId == Hmac(userId);

    private string Hmac(Guid userId) => PlayAccountKeys.Compute(Options.AccountIdSalt, userId);

    // ------------------------------------------------------------------ subscription row writes

    private static void Upsert(PlaySubscription sub, PlaySubscriptionSnapshot snapshot, PlaySyncRequest request, Mapping mapping,
        DateTime now, bool compareFields)
    {
        var primary = mapping.Primary;
        sub.ProductId = primary.ProductId;
        if (request.ClientProductId != null && snapshot.Find(request.ClientProductId) != null)
            sub.PurchasedProductId = request.ClientProductId;
        else if (string.IsNullOrEmpty(sub.PurchasedProductId))
            sub.PurchasedProductId = mapping.PurchasedProductId;
        if (mapping.Plan != null)
        {
            sub.PlanId = mapping.Plan.Id;
            sub.FeatureKey = mapping.Plan.FeatureKey;
            sub.PlanPriceId = mapping.Price?.Id;
        }
        sub.BasePlanId = primary.BasePlanId;
        sub.OfferId = primary.OfferId;
        sub.LinkedPurchaseToken = snapshot.LinkedPurchaseToken ?? sub.LinkedPurchaseToken;
        sub.ExpiredPurchaseToken = snapshot.ExpiredPurchaseToken ?? sub.ExpiredPurchaseToken;
        sub.StartTime = snapshot.StartTime;
        sub.AcknowledgementState = snapshot.AcknowledgementState;
        sub.RegionCode = snapshot.RegionCode;
        sub.IsTestPurchase = snapshot.IsTestPurchase;
        sub.ObfuscatedAccountId = snapshot.ObfuscatedAccountId ?? sub.ObfuscatedAccountId;
        sub.CancelReason = snapshot.CancelReason;
        sub.PausedAutoResumeAt = snapshot.PausedAutoResumeTime;
        sub.DeferredProductId = snapshot.Entitled != null && snapshot.DeferredProductId != snapshot.Entitled.ProductId
            ? snapshot.DeferredProductId
            : null;
        sub.RecurringPriceMicros = primary.RecurringPriceMicros;
        sub.RecurringPriceCurrency = primary.RecurringPriceCurrency;
        sub.PriceChangeState = primary.PriceChangeState;
        sub.PriceChangeMode = primary.PriceChangeMode;
        sub.LastRtdnType = request.RtdnType ?? sub.LastRtdnType;
        sub.LastEventTimeMillis = request.EventTimeMillis ?? sub.LastEventTimeMillis;
        sub.LastSyncedAt = now;
        sub.LastSyncError = null;
        sub.LastSnapshotJson = snapshot.RawJson;
        sub.UpdatedAt = now;
        if (compareFields) SetCompareFields(sub, snapshot);
    }

    private static void SetCompareFields(PlaySubscription sub, PlaySubscriptionSnapshot snapshot)
    {
        var entitled = snapshot.Entitled;
        sub.State = snapshot.State;
        sub.ExpiryTime = snapshot.ExpiryTime ?? sub.ExpiryTime;
        sub.LatestOrderId = entitled?.LatestOrderId ?? sub.LatestOrderId;
        sub.AutoRenewEnabled = entitled?.AutoRenewEnabled ?? false;
    }

    // R-DETACHED / Rejected: Google's facts only; never the user, the mapping or the queued action.
    private static void RefreshOnly(PlaySubscription sub, PlaySubscriptionSnapshot snapshot, PlaySyncRequest request, DateTime now)
    {
        sub.State = snapshot.State;
        sub.ExpiryTime = snapshot.ExpiryTime ?? sub.ExpiryTime;
        sub.LatestOrderId = snapshot.Entitled?.LatestOrderId ?? sub.LatestOrderId;
        sub.CancelReason = snapshot.CancelReason;
        sub.LastRtdnType = request.RtdnType ?? sub.LastRtdnType;
        sub.LastEventTimeMillis = request.EventTimeMillis ?? sub.LastEventTimeMillis;
        sub.LastSyncedAt = now;
        sub.LastSyncError = null;
        sub.LastSnapshotJson = snapshot.RawJson;
    }

    private static void Reject(PlaySubscription sub, string reason)
    {
        sub.MappingStatus = PlayMappingStatuses.Rejected;
        sub.UserId = null;
        sub.DetachedReason = null;
        QueueAction(sub, PlayServerActions.RevokeFull, reason);
    }

    // Never replaces an action already queued (a role-change action outranks a later rejection).
    private static void QueueAction(PlaySubscription sub, string action, string reason)
    {
        if (sub.PendingServerAction != null || NoActionStates.Contains(sub.State)) return;
        sub.PendingServerAction = action;
        sub.PendingActionReason = Truncate(reason, 300);
        sub.PendingActionAttempts = 0;
        sub.NextActionAt = null;
    }

    private async Task AfterCommitAsync(Applied applied)
    {
        try
        {
            if (applied.Apply != null)
            {
                await applier.AfterCommitAsync(applied.Apply);
                return;
            }
            foreach (var n in applied.Notifications)
                await PlanNotifications.PublishAsync(publisher, n, logger);
            if (applied.Bust is { } bust)
                await cacheInvalidator.BustForUserAsync(bust.UserId, bust.FeatureKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Play sync after-commit work failed for subscription {SubscriptionId}", applied.Result.PlaySubscriptionId);
        }
    }

    // ------------------------------------------------------------------ RTDN

    public async Task<PlayRtdnOutcome> ProcessRtdnAsync(string messageId, string dataJson, DateTime publishTime)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(dataJson);
        }
        catch (JsonException)
        {
            return PlayRtdnOutcome.Invalid;
        }
        if (root.ValueKind != JsonValueKind.Object) return PlayRtdnOutcome.Invalid;

        var packageName = Str(root, "packageName");
        var eventTime = Long(root, "eventTimeMillis");
        var subscription = Obj(root, "subscriptionNotification");
        var voided = Obj(root, "voidedPurchaseNotification");
        var chargeback = Obj(root, "pendingRefundReviewNotification");
        var test = Obj(root, "testNotification");
        var oneTime = Obj(root, "oneTimeProductNotification");

        int? type = subscription is { } sn ? (int?)Long(sn, "notificationType") : null;
        var token = subscription is { } s1 ? Str(s1, "purchaseToken") : voided is { } v1 ? Str(v1, "purchaseToken") : null;
        var orderId = voided is { } v2 ? Str(v2, "orderId") : chargeback is { } c1 ? Str(c1, "orderId") : null;
        var eventType = subscription != null ? "rtdn:" + RtdnTypeName(type)
            : voided != null ? "rtdn:voided"
            : chargeback != null ? "rtdn:chargeback_review"
            : test != null ? "rtdn:test"
            : oneTime != null ? "rtdn:one_time_product"
            : "rtdn:unknown";

        var eventId = "play:" + messageId;
        if (eventId.Length > 128) eventId = "play:" + Sha256(messageId);

        var inserted = await unitOfWork.PaymentEvents.TryInsertAsync(new PaymentEvent
        {
            EventId = eventId,
            Provider = PaymentProviders.Play,
            EventType = Truncate(eventType, 64),
            PlayPurchaseToken = token,
            PlayNotificationType = type,
            PlayOrderId = orderId,
            RawBody = dataJson,
        });
        if (!inserted)
        {
            var processed = await db.PaymentEvents.AsNoTracking()
                .Where(e => e.EventId == eventId)
                .Select(e => e.ProcessedAt)
                .FirstOrDefaultAsync();
            if (processed != null) return PlayRtdnOutcome.Duplicate;
        }

        logger.LogInformation(PlaySyncLogEvents.RtdnReceived, "Play RTDN {EventType} type {Type} token {TokenTail} message {MessageId} published {PublishTime}",
            eventType, type, PlaySyncLogEvents.Tail(token), messageId, publishTime);

        PlayRtdnOutcome outcome;
        if (packageName != Options.PackageName)
        {
            logger.LogWarning("Play RTDN {MessageId} for unknown package {PackageName} ignored", messageId, packageName);
            outcome = PlayRtdnOutcome.Ignored;
        }
        else if (subscription != null)
        {
            outcome = string.IsNullOrEmpty(token)
                ? PlayRtdnOutcome.Ignored
                : ToRtdnOutcome(await SyncTokenAsync(new PlaySyncRequest(token, PurchaseSources.Rtdn,
                    RtdnType: type, EventTimeMillis: eventTime, EventId: eventId)));
        }
        else if (voided != null)
        {
            var productType = Long(voided.Value, "productType");
            outcome = string.IsNullOrEmpty(token) || productType is 2
                ? PlayRtdnOutcome.Ignored
                : ToRtdnOutcome(await ApplyVoidedAsync(token, orderId, (int)(Long(voided.Value, "refundType") ?? 0), PurchaseSources.Rtdn));
        }
        else if (chargeback != null)
        {
            await FlagChargebackAsync(orderId);
            outcome = PlayRtdnOutcome.Processed;
        }
        else
        {
            outcome = PlayRtdnOutcome.Ignored;
        }

        await unitOfWork.PaymentEvents.SetOutcomeAsync(eventId, outcome.ToString());
        return outcome;
    }

    // ApiError Transient/PermissionDenied throws so Pub/Sub redelivers; a token Google does not know is settled.
    private static PlayRtdnOutcome ToRtdnOutcome(PlaySyncResult result) => result.Outcome switch
    {
        PlaySyncOutcome.ApiError when result.ApiError is PlayApiErrorKind.NotFound or PlayApiErrorKind.Invalid => PlayRtdnOutcome.Ignored,
        PlaySyncOutcome.ApiError => throw new InvalidOperationException($"Google Play API unavailable ({result.ApiError}); redelivery requested."),
        PlaySyncOutcome.Unmapped => PlayRtdnOutcome.Unmapped,
        PlaySyncOutcome.Rejected or PlaySyncOutcome.ProductUnknown => PlayRtdnOutcome.Rejected,
        PlaySyncOutcome.Invalid => PlayRtdnOutcome.Ignored,
        _ => PlayRtdnOutcome.Processed,
    };

    // R-CHARGEBACK: surfaced to the admin, never answered by us (N-13); money follows the later voided/revoked events.
    private async Task FlagChargebackAsync(string? orderId)
    {
        var row = orderId == null ? null : await db.PlanPurchases.AsNoTracking()
            .Where(p => p.PlayOrderId == orderId)
            .Select(p => new { p.Id, p.UserId, p.PlaySubscriptionId })
            .FirstOrDefaultAsync();
        string? token = null;
        var claimed = 0;
        if (row != null)
        {
            claimed = await db.PlanPurchases
                .Where(p => p.Id == row.Id && p.PlayChargebackReviewAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.PlayChargebackReviewAt, DateTime.UtcNow));
            if (row.PlaySubscriptionId != null)
                token = await db.PlaySubscriptions.AsNoTracking()
                    .Where(s => s.Id == row.PlaySubscriptionId)
                    .Select(s => s.PurchaseToken)
                    .FirstOrDefaultAsync();
        }
        logger.LogError(PlaySyncLogEvents.ChargebackReview,
            "Chargeback under review for Play order {OrderId} (purchase {PurchaseId}, user {UserId}, token {TokenTail}): answer in Play Console within 24 h",
            orderId, row?.Id, row?.UserId, PlaySyncLogEvents.Tail(token));
        if (row == null || claimed > 0)
            await PlanPurchaseAdminPublisher.PublishAsync(publisher,
                new PlanPurchaseAdminMessage { Kind = PlanPurchaseAdminKinds.ChargebackReview, PurchaseId = row?.Id, PlayOrderId = orderId }, logger);
    }

    // ------------------------------------------------------------------ voided / refunds

    public async Task<PlaySyncResult> ApplyVoidedAsync(string purchaseToken, string? orderId, int refundType, string source)
    {
        var note = $"Voided by Google Play (refund type {refundType})";
        var changed = orderId != null && await purchases.MarkPlayChargeRefundedAsync(orderId, note);

        var result = string.IsNullOrEmpty(purchaseToken)
            ? Result(PlaySyncOutcome.Unchanged)
            : await SyncTokenAsync(new PlaySyncRequest(purchaseToken, source));

        // The charge row may only exist after this sync (renewal order not seen before).
        if (!changed && orderId != null) changed = await purchases.MarkPlayChargeRefundedAsync(orderId, note);

        logger.LogInformation(PlaySyncLogEvents.VoidedApplied, "Play voided order {OrderId} token {TokenTail}: refunded={Changed}, sync={Outcome}",
            orderId, PlaySyncLogEvents.Tail(purchaseToken), changed, result.Outcome);

        if (!changed || orderId == null || result.Notification != null || result.Outcome == PlaySyncOutcome.ApiError) return result;

        var charge = await db.PlanPurchases.AsNoTracking()
            .Where(p => p.PlayOrderId == orderId)
            .Select(p => new { p.UserId, p.FeatureKey, p.PlaySubscriptionId, p.PlanNameSnapshot })
            .FirstOrDefaultAsync();
        if (charge?.UserId == null || charge.PlaySubscriptionId == null) return result;
        // A carry-over flip (D-09) leaves an Active non-Play row: the user is not on the free plan.
        var entitled = await db.UserPlans.AsNoTracking()
            .AnyAsync(u => u.UserId == charge.UserId && u.FeatureKey == charge.FeatureKey && u.Status == UserPlanStatuses.Active);
        if (entitled) return result;

        var n = PlanNotifications.Ended(charge.UserId.Value, charge.PlanNameSnapshot, refunded: true);
        db.NotificationEvents.Add(n);
        await db.SaveChangesAsync();
        await PlanNotifications.PublishAsync(publisher, n, logger);
        return result with { Notification = n };
    }

    public async Task<int> ReconcileVoidedAsync()
    {
        if (!client.IsConfigured) return 0;
        IReadOnlyList<PlayVoidedPurchase> voided;
        try
        {
            voided = await client.ListVoidedSubscriptionPurchasesAsync(DateTime.UtcNow - VoidedLookBack);
        }
        catch (PlayApiException ex)
        {
            logger.LogWarning(PlaySyncLogEvents.ApiError, "Play voided purchases list failed ({Kind}): {Message}", ex.Kind, ex.Message);
            return 0;
        }

        var applied = 0;
        foreach (var chunk in voided.Where(v => !string.IsNullOrEmpty(v.OrderId)).DistinctBy(v => v.OrderId).Chunk(500))
        {
            var ids = chunk.Select(v => v.OrderId).ToList();
            var open = await db.PlanPurchases.AsNoTracking()
                .Where(p => p.PlayOrderId != null && ids.Contains(p.PlayOrderId)
                    && p.Provider == PaymentProviders.Play && p.Status != PurchaseStatuses.Refunded)
                .Select(p => p.PlayOrderId!)
                .ToListAsync();
            foreach (var v in chunk.Where(v => open.Contains(v.OrderId)))
            {
                try
                {
                    await ApplyVoidedAsync(v.PurchaseToken, v.OrderId, 1, PurchaseSources.PlayReconcile);
                    applied++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Play voided reconcile failed for order {OrderId}", v.OrderId);
                }
                db.ChangeTracker.Clear();
            }
        }
        return applied;
    }

    public async Task<AdminPurchaseDto> AdminRefundPlayAsync(Guid adminId, Guid purchaseId, string reason)
    {
        if (!client.IsConfigured) throw NotConfigured();
        var p = await db.PlanPurchases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == purchaseId)
            ?? throw PlanServiceException.NotFound(PlanErrorTypes.PurchaseNotFound, "Purchase not found.");
        if (p.Status == PurchaseStatuses.Refunded) return await ToAdminDtoAsync(p);
        if (p.Provider != PaymentProviders.Play || p.Status != PurchaseStatuses.Granted
            || p.PlayOrderId == null || p.PlaySubscriptionId == null)
            throw PlanServiceException.Conflict(PlanErrorTypes.RefundNotAllowed, "This purchase cannot be refunded.");
        if (p.PayableInr <= 0)
            throw PlanServiceException.Conflict(PlanErrorTypes.RefundNotAllowed, "Use Revoke plan for a trial.");

        var latest = await unitOfWork.PlanPurchases.GetLatestGrantedBySubscriptionAsync(p.PlaySubscriptionId.Value);
        if (latest?.Id != p.Id)
            throw PlanServiceException.Conflict(PlanErrorTypes.RefundNotAllowed, "Only the latest charge of a subscription can be refunded.");

        var token = await db.PlaySubscriptions.AsNoTracking()
            .Where(s => s.Id == p.PlaySubscriptionId)
            .Select(s => s.PurchaseToken)
            .FirstOrDefaultAsync()
            ?? throw PlanServiceException.Conflict(PlanErrorTypes.RefundNotAllowed, "This purchase cannot be refunded.");

        try
        {
            await client.RevokeAsync(token, PlayRefundKind.Full);
        }
        catch (PlayApiException ex)
        {
            logger.LogError(PlaySyncLogEvents.AdminRefund, "Play refund failed ({Kind}) for purchase {PurchaseId}: {Message}", ex.Kind, p.Id, ex.Message);
            throw PlanServiceException.Gateway("Refund could not be started. Please try again.");
        }

        await purchases.MarkPlayChargeRefundedAsync(p.PlayOrderId, Truncate($"Admin refund: {reason}", 300));
        logger.LogInformation(PlaySyncLogEvents.AdminRefund, "Admin {AdminId} refunded Play purchase {PurchaseId} (order {OrderId}, token {TokenTail})",
            adminId, p.Id, p.PlayOrderId, PlaySyncLogEvents.Tail(token));

        try
        {
            await SyncTokenAsync(new PlaySyncRequest(token, PurchaseSources.AdminRefund));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sync after admin refund of purchase {PurchaseId} failed; the reconciler settles it", p.Id);
        }
        db.ChangeTracker.Clear();
        var fresh = await db.PlanPurchases.AsNoTracking().FirstAsync(x => x.Id == purchaseId);
        return await ToAdminDtoAsync(fresh);
    }

    private async Task<AdminPurchaseDto> ToAdminDtoAsync(PlanPurchase p)
    {
        var phone = p.UserId == null ? null : await db.Users.AsNoTracking()
            .Where(u => u.Id == p.UserId)
            .Select(u => u.PhoneNumber)
            .FirstOrDefaultAsync();
        return PlanPurchaseService.ToAdminDto(p, phone);
    }

    // ------------------------------------------------------------------ reconciler passes

    // P1: cleared only after a Google 2xx (or a re-get proving the token already ended); never marks Refunded.
    public async Task<int> RunPendingServerActionsAsync(int batchSize)
    {
        if (!client.IsConfigured) return 0;
        var now = DateTime.UtcNow;
        var ids = await subscriptions.ListPendingActionsAsync(now, batchSize);
        var executed = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            var sub = await db.PlaySubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            if (sub?.PendingServerAction is not { } action) continue;

            bool done;
            string? error = null;
            try
            {
                switch (action)
                {
                    case PlayServerActions.Cancel:
                        await client.CancelAsync(sub.PurchaseToken, PlayCancelKind.DeveloperRequestedStopPayments);
                        break;
                    case PlayServerActions.RevokeFull:
                        await client.RevokeAsync(sub.PurchaseToken, PlayRefundKind.Full);
                        break;
                    case PlayServerActions.RevokeProrated:
                        await client.RevokeAsync(sub.PurchaseToken, PlayRefundKind.Prorated);
                        break;
                    default:
                        throw new PlayApiException(PlayApiErrorKind.Invalid, $"Unknown server action {action}");
                }
                done = true;
            }
            catch (PlayApiException ex)
            {
                error = $"{ex.Kind}: {ex.Message}";
                done = (ex.Kind is PlayApiErrorKind.Invalid or PlayApiErrorKind.NotFound)
                    && await AlreadyEndedAsync(sub.PurchaseToken, action);
            }

            if (done)
            {
                await db.PlaySubscriptions
                    .Where(s => s.Id == id && s.PendingServerAction == action)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(s => s.PendingServerAction, (string?)null)
                        .SetProperty(s => s.NextActionAt, (DateTime?)null)
                        .SetProperty(s => s.UpdatedAt, DateTime.UtcNow));
                executed++;
                logger.LogInformation(PlaySyncLogEvents.ActionExecuted, "Play {Action} executed for token {TokenTail} after {Attempts} failed attempts",
                    action, PlaySyncLogEvents.Tail(sub.PurchaseToken), sub.PendingActionAttempts);
                try
                {
                    await SyncTokenAsync(new PlaySyncRequest(sub.PurchaseToken, PurchaseSources.PlayReconcile));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Sync after {Action} failed for token {TokenTail}", action, PlaySyncLogEvents.Tail(sub.PurchaseToken));
                }
                continue;
            }

            var attempts = sub.PendingActionAttempts + 1;
            var next = DateTime.UtcNow.AddMinutes(Math.Min(Math.Pow(2, Math.Min(attempts, 6)), 60));
            await db.PlaySubscriptions
                .Where(s => s.Id == id && s.PendingServerAction == action)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.PendingActionAttempts, attempts)
                    .SetProperty(s => s.NextActionAt, next)
                    .SetProperty(s => s.LastSyncError, Truncate(error ?? "Server action failed", 300)));
            if (attempts >= MaxActionAttemptsBeforeAlarm)
                logger.LogError(PlaySyncLogEvents.ActionFailed, "Play {Action} for token {TokenTail} still failing after {Attempts} attempts: {Error}",
                    action, PlaySyncLogEvents.Tail(sub.PurchaseToken), attempts, error);
            else
                logger.LogWarning(PlaySyncLogEvents.ActionFailed, "Play {Action} for token {TokenTail} failed (attempt {Attempts}): {Error}",
                    action, PlaySyncLogEvents.Tail(sub.PurchaseToken), attempts, error);
        }
        return executed;
    }

    // Google errors on a token that is already revoked/cancelled; a fresh get decides.
    private async Task<bool> AlreadyEndedAsync(string token, string action)
    {
        try
        {
            var snapshot = await client.GetSubscriptionAsync(token);
            return snapshot.State is PlayStates.Expired or PlayStates.PendingPurchaseCanceled
                   || action == PlayServerActions.Cancel && snapshot.State == PlayStates.Canceled;
        }
        catch (PlayApiException)
        {
            return false;
        }
    }

    // P2: entitled-looking tokens past their expiry (missed RENEWED/EXPIRED).
    public async Task<int> SyncDueAsync(int batchSize)
    {
        if (!client.IsConfigured) return 0;
        var ids = await subscriptions.ListDueAsync(DateTime.UtcNow - DueGrace, batchSize);
        return await SyncIdsAsync(ids);
    }

    // P4: missed-RTDN safety net.
    public async Task<int> SyncStaleAsync(int batchSize)
    {
        if (!client.IsConfigured) return 0;
        var ids = await subscriptions.ListStaleAsync(DateTime.UtcNow - PlayRules.StaleSyncAge, batchSize);
        return await SyncIdsAsync(ids);
    }

    private async Task<int> SyncIdsAsync(List<Guid> ids)
    {
        var changed = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            var token = await db.PlaySubscriptions.AsNoTracking().Where(s => s.Id == id).Select(s => s.PurchaseToken).FirstOrDefaultAsync();
            if (token == null) continue;
            try
            {
                var result = await SyncTokenAsync(new PlaySyncRequest(token, PurchaseSources.PlayReconcile));
                if (result.Outcome is not (PlaySyncOutcome.Unchanged or PlaySyncOutcome.ApiError)) changed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Play reconcile sync failed for subscription {SubscriptionId}", id);
            }
        }
        db.ChangeTracker.Clear();
        return changed;
    }

    // ------------------------------------------------------------------ helpers

    private static PlaySyncResult Result(
        PlaySyncOutcome outcome, Guid? subscriptionId = null, Guid? purchaseId = null, Guid? userPlanId = null, Guid? userId = null,
        string? feature = null, string? blocked = null, PlayApiErrorKind? apiError = null, NotificationEvent? notification = null,
        DateTime? expiry = null)
        => new(outcome, subscriptionId, purchaseId, userPlanId, userId, feature, blocked, apiError, notification, expiry);

    private static PlanServiceException NotConfigured()
        => new(503, PlanErrorTypes.PlayNotConfigured, "Google Play billing is not configured.");

    private static string PurchaseSourceTag(string source) => source switch
    {
        PurchaseSources.VerifyPlay or PurchaseSources.Restore or PurchaseSources.Rtdn => source,
        _ => PurchaseSources.PlayReconcile,
    };

    private static string RtdnTypeName(int? type) => type switch
    {
        PlayRtdnTypes.Recovered => "recovered",
        PlayRtdnTypes.Renewed => "renewed",
        PlayRtdnTypes.Canceled => "canceled",
        PlayRtdnTypes.Purchased => "purchased",
        PlayRtdnTypes.OnHold => "on_hold",
        PlayRtdnTypes.InGracePeriod => "in_grace_period",
        PlayRtdnTypes.Restarted => "restarted",
        PlayRtdnTypes.PriceChangeConfirmed => "price_change_confirmed",
        PlayRtdnTypes.Deferred => "deferred",
        PlayRtdnTypes.Paused => "paused",
        PlayRtdnTypes.PauseScheduleChanged => "pause_schedule_changed",
        PlayRtdnTypes.Revoked => "revoked",
        PlayRtdnTypes.Expired => "expired",
        PlayRtdnTypes.ItemsChanged => "items_changed",
        PlayRtdnTypes.CancellationScheduled => "cancellation_scheduled",
        PlayRtdnTypes.PriceChangeUpdated => "price_change_updated",
        PlayRtdnTypes.PendingPurchaseCanceled => "pending_purchase_canceled",
        PlayRtdnTypes.PriceStepUpConsentUpdated => "price_step_up_consent_updated",
        null => "subscription",
        _ => $"type_{type}",
    };

    private static JsonElement? Obj(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Pub/Sub JSON carries int64 as strings ("eventTimeMillis") and small ints as numbers.
    private static long? Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static bool IsUniqueViolation(DbUpdateException ex, string constraint)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg && pg.ConstraintName == constraint;

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}
