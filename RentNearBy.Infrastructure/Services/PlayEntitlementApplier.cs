// DI (ServiceCollectionExtensions):
//   services.AddScoped<IPlayEntitlementApplier, PlayEntitlementApplier>();
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// D-11: obfuscatedAccountId = HMAC-SHA256(salt, userId) as 64 lower-hex chars.
public static class PlayAccountKeys
{
    public static string Compute(string salt, Guid userId)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes(userId.ToString("D"))))
            .ToLowerInvariant();
}

// The entitled rows of contract 3.1 (R-REATTACH, R-NEW, R-RENEW, R-UPDATE, R-SWITCH, R-RECOVER, R-GRACE,
// R-CANCELED). Runs inside the sync transaction under the owner and token locks; Google is only called after
// commit (AfterCommitAsync / TryAcknowledgeAsync). The snapshot is read only through Entitled / DeferredProductId.
public class PlayEntitlementApplier(
    ApplicationDbContext db,
    IUnitOfWork unitOfWork,
    IPlanPurchaseService purchases,
    IPlayBillingClient client,
    IPlaySubscriptionRepository subscriptions,
    IPlanEntitlementService entitlements,
    ILiveSlotService slots,
    IListingCacheInvalidator cacheInvalidator,
    IRabbitMqPublisher publisher,
    ILogger<PlayEntitlementApplier> logger) : IPlayEntitlementApplier
{
    public async Task<PlayApplyResult> ApplyEntitledInTxAsync(PlaySubscription subscription, PlaySubscriptionSnapshot snapshot,
        PlayResolvedTarget target, PlanPurchase? openPurchase, string source)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("ApplyEntitledInTxAsync requires the caller's transaction and locks.");
        var item = snapshot.Entitled
            ?? throw new InvalidOperationException("An entitled snapshot must have a line item with an expiryTime.");
        if (!snapshot.IsEntitled)
            throw new InvalidOperationException($"Snapshot state {snapshot.State} is not entitled.");

        var now = DateTime.UtcNow;
        var sub = Track(subscription);
        if (target.Reattach && sub.MappingStatus == PlayMappingStatuses.Detached)
        {
            sub.MappingStatus = PlayMappingStatuses.Mapped;
            sub.DetachedReason = null;
            sub.UpdatedAt = now;
            await db.SaveChangesAsync();
            logger.LogInformation(new EventId(0, "play.reattach"), "Play subscription {SubscriptionId} re-attached to user {UserId}",
                sub.Id, target.UserId);
        }
        if (sub.MappingStatus != PlayMappingStatuses.Mapped)
            throw new InvalidOperationException($"Play subscription {sub.Id} is {sub.MappingStatus}; only Mapped tokens are applied.");

        var userId = target.UserId;
        var feature = target.FeatureKey;
        var endsAt = item.ExpiryTime!.Value;
        var autoRenew = snapshot.State != PlayStates.Canceled && item.AutoRenewEnabled == true;
        var scheduledPlanId = target.ScheduledPlan is { } scheduled && scheduled.Id != target.Plan.Id ? scheduled.Id : (Guid?)null;
        if (snapshot.DeferredProductId != null && target.ScheduledPlan == null)
            logger.LogWarning(new EventId(0, "play.deferred.unknown_product"),
                "Deferred change to unknown product {ProductId} on subscription {SubscriptionId}", snapshot.DeferredProductId, sub.Id);
        var isTrialOffer = item.OfferId != null && item.OfferId == target.Price.PlayTrialOfferId;
        var needsAck = snapshot.AcknowledgementState == PlayAckStates.Pending && sub.AcknowledgedAt == null;

        foreach (var entry in db.ChangeTracker.Entries<UserPlan>().Where(e => e.Entity.UserId == userId).ToList())
            entry.State = EntityState.Detached;
        var active = await db.UserPlans.FirstOrDefaultAsync(u =>
            u.UserId == userId && u.FeatureKey == feature && u.Status == UserPlanStatuses.Active);

        var orderId = item.LatestOrderId;
        var existingCharge = orderId == null ? null : await db.PlanPurchases.FirstOrDefaultAsync(p => p.PlayOrderId == orderId);

        PlayGrantCommand Command(int paidInr, PlanPurchase? purchase, DateTime? trialEndsAt, int carryFallback = 0) => new(
            userId, feature, target.Plan.Id, DaysOf(target, isTrialOffer && trialEndsAt != null), paidInr, endsAt, sub.Id, autoRenew,
            snapshot.State, trialEndsAt, scheduledPlanId, purchase, target.Plan.Name, CarryOverDaysFallback: carryFallback);

        // R-RENEW / R-UPDATE / R-GRACE / R-CANCELED: this token already owns the Active row.
        if (active != null && active.PlaySubscriptionId == sub.Id)
        {
            if (orderId != null && existingCharge == null)
            {
                var charge = await CreateChargeAsync(sub, item, target, PurchaseIntents.Renew, false, source, null, now);
                var renewed = await purchases.GrantPlayInTxAsync(Command(charge.PayableInr, charge, null));
                return Result(PlayApplyKind.Renewed, sub, target, renewed.UserPlan, charge, Persisted(renewed.Notification), needsAck);
            }
            var notification = await UpdateOwnRowAsync(active, snapshot, target, endsAt, autoRenew, scheduledPlanId, now);
            return Result(PlayApplyKind.Unchanged, sub, target, active, null, notification, needsAck);
        }

        // R-SWITCH: the Active row belongs to another Play token this one replaced (upgrade/downgrade/deferred).
        if (active?.PlaySubscriptionId is { } otherId)
        {
            var other = await db.PlaySubscriptions.AsNoTracking()
                .Where(s => s.Id == otherId)
                .Select(s => new { s.PurchaseToken, s.MappingStatus, s.State, s.ExpiryTime })
                .FirstOrDefaultAsync();
            var linked = other != null && snapshot.LinkedPurchaseToken == other.PurchaseToken;
            var otherLive = other != null && other.MappingStatus == PlayMappingStatuses.Mapped && PlayStates.IsEntitled(other.State, other.ExpiryTime);
            if (!linked && otherLive)
            {
                // Two live subscriptions for one feature: never grant or acknowledge the second (Google refunds an
                // unacknowledged purchase after 3 days; play.ack.overdue alerts a human first).
                logger.LogError(new EventId(0, "play.sync.duplicate_subscription"),
                    "User {UserId} has a second live Play subscription {SubscriptionId} for {Feature}; not granted",
                    userId, sub.Id, feature);
                return Result(PlayApplyKind.Unchanged, sub, target, active, null, null, false);
            }

            PlanPurchase? switchCharge = null;
            if (orderId != null && existingCharge == null)
                switchCharge = await CreateChargeAsync(sub, item, target, PurchaseIntents.Switch, isTrialOffer, source, openPurchase, now);
            var switched = await purchases.GrantPlayInTxAsync(Command(switchCharge?.PayableInr ?? 0, switchCharge,
                switchCharge != null && isTrialOffer ? endsAt : null));
            if (linked && other!.MappingStatus == PlayMappingStatuses.Mapped)
                await db.PlaySubscriptions
                    .Where(s => s.Id == otherId && s.MappingStatus == PlayMappingStatuses.Mapped)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.MappingStatus, PlayMappingStatuses.Detached)
                        .SetProperty(x => x.DetachedReason, PlayDetachReasons.Replaced)
                        .SetProperty(x => x.UpdatedAt, now));
            var scheduledOrder = switchCharge == null
                ? await SettleDeferredOrderAsync(openPurchase, sub, switched.UserPlan.Id, source, now)
                : null;
            return Result(PlayApplyKind.Switched, sub, target, switched.UserPlan, switchCharge ?? scheduledOrder,
                Persisted(switched.Notification), needsAck);
        }

        // R-REATTACH / R-RECOVER / R-NEW: no Play row is Active for this feature (a non-Play row is switched in place
        // with carry-over by GrantPlayInTxAsync).
        var previous = await db.UserPlans.AsNoTracking()
            .Where(u => u.PlaySubscriptionId == sub.Id && u.Status != UserPlanStatuses.Active)
            .OrderByDescending(u => u.UpdatedAt)
            .Select(u => new { u.CarryOverDays })
            .FirstOrDefaultAsync();
        // The hold/pause carry-over flip nulls the row's PlaySubscriptionId, so its recovery is found by the token's
        // earlier granted charge.
        var flippedRecovery = previous == null && !target.Reattach
            && active is { PlaySubscriptionId: null, Source: UserPlanSources.AdminGrant, Note: "Carry-over" }
            && await db.PlanPurchases.AsNoTracking().AnyAsync(p => p.PlaySubscriptionId == sub.Id && p.GrantedAt != null);
        PlayApplyKind kind;
        if (target.Reattach) kind = PlayApplyKind.Reattached;
        else if (previous != null || flippedRecovery) kind = PlayApplyKind.Recovered;
        else if (await db.PlaySubscriptions.AsNoTracking().AnyAsync(s => s.UserId == userId && s.FeatureKey == feature && s.Id != sub.Id))
            kind = PlayApplyKind.Resubscribed;
        else kind = PlayApplyKind.Granted;

        PlanPurchase? newCharge = null;
        PlanPurchase? grantPurchase = null;
        DateTime? trialEndsAt = null;
        if (orderId != null && existingCharge == null)
        {
            var intent = kind == PlayApplyKind.Recovered ? PurchaseIntents.Renew : PurchaseIntents.New;
            var trialCharge = isTrialOffer && kind is PlayApplyKind.Granted or PlayApplyKind.Resubscribed;
            newCharge = await CreateChargeAsync(sub, item, target, intent, trialCharge,
                kind == PlayApplyKind.Reattached ? PurchaseSources.Restore : source,
                kind == PlayApplyKind.Recovered ? null : openPurchase, now);
            grantPurchase = newCharge;
            if (trialCharge) trialEndsAt = endsAt;
        }
        else if (existingCharge is { Status: PurchaseStatuses.Created or PurchaseStatuses.Pending })
        {
            grantPurchase = existingCharge;
        }

        var granted = await purchases.GrantPlayInTxAsync(Command(newCharge?.PayableInr ?? 0, grantPurchase, trialEndsAt,
            previous?.CarryOverDays ?? 0));
        return Result(kind, sub, target, granted.UserPlan, newCharge ?? grantPurchase, Persisted(granted.Notification), needsAck);
    }

    // R-UPDATE / R-GRACE / R-CANCELED on the row this token owns (same order: no charge, no grant push).
    private async Task<NotificationEvent?> UpdateOwnRowAsync(UserPlan row, PlaySubscriptionSnapshot snapshot, PlayResolvedTarget target,
        DateTime endsAt, bool autoRenew, Guid? scheduledPlanId, DateTime now)
    {
        var prevEnds = row.EndsAt;
        var prevState = row.PlayState;
        var planChanged = row.PlanId != target.Plan.Id;
        var autoRenewStopped = row.PlayAutoRenewEnabled != false && !autoRenew;
        var changed = planChanged || endsAt != prevEnds || row.PlayAutoRenewEnabled != autoRenew
            || row.PlayState != snapshot.State || row.PlayScheduledPlanId != scheduledPlanId;

        NotificationEvent? notification = null;
        if (changed)
        {
            if (endsAt != prevEnds || autoRenewStopped) row.ReminderSentAt = null;
            if (endsAt > prevEnds) row.TotalDays += Math.Max(0, (int)(endsAt - prevEnds).TotalDays);
            row.EndsAt = endsAt;
            row.PlanId = target.Plan.Id;
            row.PlayAutoRenewEnabled = autoRenew;
            row.PlayState = snapshot.State;
            row.PlayScheduledPlanId = scheduledPlanId;
            row.UpdatedAt = now;
            await db.SaveChangesAsync();

            if (planChanged)
            {
                await slots.ApplyGrantEffectsAsync(row.UserId, row.FeatureKey);
            }
            else if (endsAt > prevEnds && PlanFeatures.HasListingSlots(row.FeatureKey))
            {
                var validUntil = await entitlements.ResolveValidUntilAsync(row.UserId, row.FeatureKey);
                await slots.ExtendValidUntilAsync(row.UserId, row.FeatureKey, validUntil);
            }
        }

        if (snapshot.State == PlayStates.InGracePeriod && prevState != PlayStates.InGracePeriod)
        {
            notification = PlanNotifications.PaymentIssue(row.UserId, target.Plan.Name);
            db.NotificationEvents.Add(notification);
            await db.SaveChangesAsync();
        }
        return notification;
    }

    // Charge row of the entitled item's order (D-17). The first one reuses the user's open Play order of the same
    // plan+cycle; an open order of another plan/cycle is closed. R-PRICE for PayableInr.
    private async Task<PlanPurchase> CreateChargeAsync(PlaySubscription sub, PlaySubscriptionLineItem item, PlayResolvedTarget target,
        string intent, bool isTrial, string source, PlanPurchase? openPurchase, DateTime now)
    {
        var userId = target.UserId;
        var cycle = target.Price.CycleMonths;
        var list = target.Price.PriceInr;
        var payable = isTrial ? 0 : PayableOf(item, list);

        // Renewal and recovery charges are Google-initiated: they never adopt a user's open order.
        var firstCharge = intent != PurchaseIntents.Renew;
        Guid? reuseId = null;
        if (firstCharge && openPurchase is { } op && op.UserId == userId && op.FeatureKey == target.FeatureKey
            && op.Provider == PaymentProviders.Play && op.PlayOrderId == null
            && op.Status is PurchaseStatuses.Created or PurchaseStatuses.Pending)
        {
            if (op.PlanId == target.Plan.Id && op.CycleMonths == cycle) reuseId = op.Id;
            else await CloseReplacedAsync(op.Id);
        }
        if (firstCharge && reuseId == null)
        {
            var sameOrder = await unitOfWork.PlanPurchases.GetOpenPlayByUserFeatureAsync(userId, target.FeatureKey, target.Plan.Id, cycle);
            if (sameOrder is { PlayOrderId: null }) reuseId = sameOrder.Id;
        }

        PlanPurchase? charge = null;
        if (reuseId is { } id)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"PlanPurchases\" WHERE \"Id\" = {id} FOR UPDATE");
            foreach (var entry in db.ChangeTracker.Entries<PlanPurchase>().Where(e => e.Entity.Id == id).ToList())
                entry.State = EntityState.Detached;
            charge = await db.PlanPurchases.FirstOrDefaultAsync(p => p.Id == id
                && (p.Status == PurchaseStatuses.Created || p.Status == PurchaseStatuses.Pending) && p.PlayOrderId == null);
        }

        if (charge == null)
        {
            charge = new PlanPurchase
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                FeatureKey = target.FeatureKey,
                Provider = PaymentProviders.Play,
                Currency = "INR",
                // Paid (not open): ix_planpurchases_one_open stays free; GrantPlayInTxAsync marks it Granted.
                Status = PurchaseStatuses.Paid,
                CreatedAt = now,
                ExpiresAt = now,
            };
            db.PlanPurchases.Add(charge);
        }

        charge.PlanId = target.Plan.Id;
        charge.PlanNameSnapshot = target.Plan.Name;
        charge.CycleMonths = cycle;
        charge.GrantedDays = DaysOf(target, isTrial);
        charge.Intent = intent;
        charge.ListPriceInr = list;
        charge.DiscountInr = isTrial ? list : 0;
        charge.CreditInr = 0;
        charge.PayableInr = payable;
        charge.PaidAt ??= now;
        charge.PlaySubscriptionId = sub.Id;
        charge.PlayOrderId = item.LatestOrderId;
        charge.PlayProductId = item.ProductId;
        charge.PlayBasePlanId = item.BasePlanId;
        charge.PlayOfferId = item.OfferId;
        charge.PlayPurchaseSource ??= source;
        charge.FailureReason = null;
        charge.GrantBlockedReason = null;
        await db.SaveChangesAsync();
        return charge;
    }

    // A deferred downgrade charges nothing now (the old order keeps its charge row; the first renewal under the new
    // product writes the next one). Its open order is closed as a Rs 0 Granted record so it never resurfaces as open.
    private async Task<PlanPurchase?> SettleDeferredOrderAsync(PlanPurchase? openPurchase, PlaySubscription sub, Guid userPlanId,
        string source, DateTime now)
    {
        if (openPurchase is not { Provider: PaymentProviders.Play, PlayOrderId: null, Intent: PurchaseIntents.Switch } op)
            return null;
        var settled = await db.PlanPurchases
            .Where(p => p.Id == op.Id && p.Provider == PaymentProviders.Play && p.PlayOrderId == null
                && (p.Status == PurchaseStatuses.Created || p.Status == PurchaseStatuses.Pending))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PurchaseStatuses.Granted)
                .SetProperty(p => p.GrantedAt, now)
                .SetProperty(p => p.GrantedUserPlanId, userPlanId)
                .SetProperty(p => p.PlaySubscriptionId, sub.Id)
                .SetProperty(p => p.PlayPurchaseSource, source)
                .SetProperty(p => p.PayableInr, 0)
                .SetProperty(p => p.DiscountInr, 0)
                .SetProperty(p => p.GrantedDays, 0)) > 0;
        return settled ? await db.PlanPurchases.AsNoTracking().FirstOrDefaultAsync(p => p.Id == op.Id) : null;
    }

    private async Task CloseReplacedAsync(Guid purchaseId)
        => await db.PlanPurchases
            .Where(p => p.Id == purchaseId && p.Provider == PaymentProviders.Play && p.PlayOrderId == null
                && (p.Status == PurchaseStatuses.Created || p.Status == PurchaseStatuses.Pending))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PurchaseStatuses.Expired)
                .SetProperty(p => p.FailureReason, "Replaced by a purchase"));

    // R-PRICE: what Google charges when it is INR, else the admin mirror.
    internal static int PayableOf(PlaySubscriptionLineItem item, int mirror)
        => item.RecurringPriceCurrency == "INR" && item.RecurringPriceMicros is > 0
            ? (int)Math.Round(item.RecurringPriceMicros.Value / 1_000_000m, MidpointRounding.AwayFromZero)
            : mirror;

    internal static int DaysOf(PlayResolvedTarget target, bool trial)
        => trial ? Math.Max(1, target.Plan.TrialDays) : PlanCycles.DaysOf(target.Price.CycleMonths);

    // GrantPlayInTxAsync does not write a push row for a switch that keeps the plan (deferred downgrade).
    private NotificationEvent? Persisted(NotificationEvent notification)
        => db.Entry(notification).State == EntityState.Detached ? null : notification;

    private PlaySubscription Track(PlaySubscription subscription)
    {
        var local = db.PlaySubscriptions.Local.FirstOrDefault(s => s.Id == subscription.Id);
        if (local != null) return local;
        db.PlaySubscriptions.Attach(subscription);
        return subscription;
    }

    private static PlayApplyResult Result(PlayApplyKind kind, PlaySubscription sub, PlayResolvedTarget target, UserPlan userPlan,
        PlanPurchase? charge, NotificationEvent? notification, bool needsAck)
        => new(kind, sub.Id, target.UserId, target.FeatureKey, userPlan, charge, notification, needsAck,
            target.PurchasedProductId, sub.PurchaseToken);

    // ------------------------------------------------------------------ after commit

    public async Task AfterCommitAsync(PlayApplyResult result)
    {
        if (db.Database.CurrentTransaction != null)
            throw new InvalidOperationException("AfterCommitAsync must run after the sync transaction is committed.");
        if (result.Notification != null)
            await PlanNotifications.PublishAsync(publisher, result.Notification, logger);
        if (result.ChargeRow is { Status: PurchaseStatuses.Granted } charge
            && (charge.PayableInr > 0 || charge.PlayOfferId != null)
            && PlanPurchaseAdminPublisher.KindOf(result.Kind) is { } kind)
            await PlanPurchaseAdminPublisher.PublishAsync(publisher,
                new PlanPurchaseAdminMessage { Kind = kind, PurchaseId = charge.Id, PlayOrderId = charge.PlayOrderId }, logger);
        await cacheInvalidator.BustForUserAsync(result.UserId, result.FeatureKey);
        if (result.NeedsAcknowledge)
            await AcknowledgeAsync(result.PlaySubscriptionId, result.PurchaseToken, result.PurchasedProductId);
    }

    public async Task<bool> TryAcknowledgeAsync(Guid playSubscriptionId)
    {
        if (db.Database.CurrentTransaction != null)
            throw new InvalidOperationException("Google calls never run inside a transaction.");
        var sub = await db.PlaySubscriptions.AsNoTracking()
            .Where(s => s.Id == playSubscriptionId)
            .Select(s => new { s.PurchaseToken, s.PurchasedProductId, s.MappingStatus, s.AcknowledgedAt })
            .FirstOrDefaultAsync();
        if (sub == null) return false;
        if (sub.AcknowledgedAt != null) return true;
        if (sub.MappingStatus != PlayMappingStatuses.Mapped) return false;
        // Only a token that was actually granted is kept (D-12); a duplicate second subscription stays unacknowledged.
        if (!await db.UserPlans.AsNoTracking().AnyAsync(u => u.PlaySubscriptionId == playSubscriptionId))
        {
            logger.LogWarning(new EventId(0, "play.ack.failed"),
                "Play subscription {SubscriptionId} owns no plan row; not acknowledged (token {TokenTail})",
                playSubscriptionId, PlayBillingClient.TokenTail(sub.PurchaseToken));
            return false;
        }
        return await AcknowledgeAsync(playSubscriptionId, sub.PurchaseToken, sub.PurchasedProductId);
    }

    // R-ACKID: PurchasedProductId, never the client's string. "Already acknowledged" errors are settled by a re-get.
    private async Task<bool> AcknowledgeAsync(Guid playSubscriptionId, string purchaseToken, string productId)
    {
        var tail = PlayBillingClient.TokenTail(purchaseToken);
        if (string.IsNullOrEmpty(productId))
        {
            logger.LogError(new EventId(0, "play.ack.failed"), "Play subscription {SubscriptionId} has no purchased product id (token {TokenTail})",
                playSubscriptionId, tail);
            return false;
        }
        try
        {
            await client.AcknowledgeAsync(purchaseToken, productId);
            await subscriptions.MarkAcknowledgedAsync(playSubscriptionId, DateTime.UtcNow);
            logger.LogInformation(new EventId(0, "play.ack.ok"), "Play subscription {SubscriptionId} acknowledged (token {TokenTail})",
                playSubscriptionId, tail);
            return true;
        }
        catch (PlayApiException ex)
        {
            if (ex.Kind != PlayApiErrorKind.Transient)
            {
                try
                {
                    var current = await client.GetSubscriptionAsync(purchaseToken);
                    if (current.AcknowledgementState == PlayAckStates.Acknowledged)
                    {
                        await subscriptions.MarkAcknowledgedAsync(playSubscriptionId, DateTime.UtcNow);
                        return true;
                    }
                }
                catch (PlayApiException)
                {
                }
            }
            await db.PlaySubscriptions
                .Where(s => s.Id == playSubscriptionId && s.AcknowledgedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AckAttempts, x => x.AckAttempts + 1));
            logger.LogWarning(new EventId(0, "play.ack.failed"), ex,
                "Play acknowledge failed ({Kind}) for subscription {SubscriptionId} (token {TokenTail}); the reconciler retries",
                ex.Kind, playSubscriptionId, tail);
            return false;
        }
    }
}
