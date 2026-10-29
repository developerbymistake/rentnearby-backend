using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Plan notification rows (inbox + FCM via notification.push). Hosted services publish RabbitMQ only;
// API paths may additionally push the returned row over SignalR.
public static class PlanNotifications
{
    public const string Queue = "notification.push";

    private static string Day(DateTime utc) => utc.AddMinutes(330).ToString("dd MMM yyyy");

    public static NotificationEvent Activated(Guid userId, string planName, DateTime endsAt) =>
        Make(userId, NotificationTypes.PlanActivated, "Plan active", $"{planName} is active till {Day(endsAt)}.");

    public static NotificationEvent Expiring(Guid userId, string planName, DateTime endsAt) =>
        Make(userId, NotificationTypes.PlanExpiring, "Plan ends soon", $"{planName} ends on {Day(endsAt)}. Renew to keep your benefits.");

    public static NotificationEvent Expired(Guid userId, string planName) =>
        Make(userId, NotificationTypes.PlanExpired, "Plan ended", $"{planName} has ended. You are on the free plan now.");

    private static NotificationEvent Make(Guid userId, string type, string title, string body) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = type,
        Title = title,
        Body = body,
        ActionRoute = UserNotificationRoutes.Plans,
        CreatedAt = DateTime.UtcNow,
    };

    // Best-effort, after commit.
    public static async Task PublishAsync(IRabbitMqPublisher publisher, NotificationEvent notification, ILogger logger)
    {
        if (notification.TargetUserId == null) return;
        try
        {
            var payload = JsonSerializer.Serialize(new NotificationPushPayload(notification.Id, notification.TargetUserId.Value));
            await publisher.PublishAsync(Queue, payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Plan push publish failed for notification {NotificationId}", notification.Id);
        }
    }
}

public class PlanPurchaseService(
    ApplicationDbContext db,
    IUnitOfWork unitOfWork,
    IMemoryCache memoryCache,
    IPlanCatalogCache catalog,
    IPaymentFeatureReader payment,
    IPlanEntitlementService entitlements,
    ILiveSlotService slots,
    IListingCacheInvalidator cacheInvalidator,
    IRazorpayService razorpay,
    IRateLimitService rateLimiter,
    IRabbitMqPublisher publisher,
    ILogger<PlanPurchaseService> logger) : IPlanPurchaseService
{
    private const string CouponInvalidMessage = "This coupon cannot be used.";
    private const string Captured = "captured";
    private static readonly string[] StillResolving = ["created", "authorized"];

    private sealed record UserInfo(Guid Id, Guid RoleId, DateTime CreatedAt);

    private sealed record QuoteCalc(
        CatalogPlan Plan,
        CatalogPrice Price,
        string FeatureKey,
        string Intent,
        int ListPriceInr,
        int DiscountInr,
        int CreditInr,
        int PayableInr,
        int GrantedDays,
        Coupon? Coupon,
        bool AutoCoupon);

    private sealed record GrantOutcome(UserPlan UserPlan, NotificationEvent Notification);

    // ------------------------------------------------------------------ reads

    public async Task<PlanCatalogDto> GetCatalogAsync(string? featureKey)
    {
        var snapshot = await catalog.GetAsync();
        var dto = new PlanCatalogDto();
        foreach (var feature in PlanFeatures.All)
        {
            if (featureKey != null && feature != featureKey) continue;
            var plans = snapshot.PlansOf(feature).Where(p => p.IsEnabled).ToList();
            if (plans.Count == 0) continue;
            dto.Features.Add(new PlanCatalogFeatureDto
            {
                FeatureKey = feature,
                Plans = plans.Select(ToCatalogItem).ToList(),
            });
        }
        return dto;
    }

    private static PlanCatalogItemDto ToCatalogItem(CatalogPlan p)
    {
        var prices = p.Prices.Where(x => x.IsEnabled).OrderBy(x => x.CycleMonths).ToList();
        var monthly = prices.FirstOrDefault(x => x.CycleMonths == 1)?.PriceInr;
        return new PlanCatalogItemDto
        {
            Id = p.Id,
            Code = p.Code,
            Name = p.Name,
            Tagline = p.Tagline,
            IsDefault = p.IsDefault,
            IsEnabled = p.IsEnabled,
            TrialDays = p.TrialDays,
            Features = PlanFeatureKeys.All.ToDictionary(k => k, k => p.Features.GetValueOrDefault(k)),
            Prices = prices.Select(x =>
            {
                var perMonth = x.PriceInr / Math.Max(1, (int)x.CycleMonths);
                var save = monthly is > 0 && x.CycleMonths > 1
                    ? (int)Math.Round((1 - (double)x.PriceInr / (monthly.Value * x.CycleMonths)) * 100)
                    : 0;
                return new PlanPriceDto
                {
                    CycleMonths = x.CycleMonths,
                    PriceInr = x.PriceInr,
                    PerMonthInr = perMonth,
                    SavePercent = Math.Max(0, save),
                };
            }).ToList(),
        };
    }

    public async Task<MyPlanDto> GetMyPlanAsync(Guid userId)
    {
        var user = await GetUserAsync(userId);
        var flag = await payment.GetAsync();
        var feature = UserRoles.ListingKindOf(user.RoleId);
        var dto = new MyPlanDto { PaymentEnabled = flag.Enabled, FreeDays = flag.FreeDays, FeatureKey = feature };
        if (feature == null) return dto;

        var limits = await entitlements.GetLimitsAsync(userId, feature);
        dto.Plan = new MyPlanRefDto { Id = limits.PlanId, Name = limits.PlanName, IsDefault = limits.IsDefaultPlan };
        dto.IsPaid = limits.IsPaid;
        dto.Source = limits.Source;
        dto.Status = limits.UserPlanId != null ? UserPlanStatuses.Active : null;
        dto.StartsAt = limits.StartsAt;
        dto.EndsAt = limits.EndsAt;
        dto.DaysLeft = limits.EndsAt == null ? null : Math.Max(0, (int)Math.Ceiling((limits.EndsAt.Value - DateTime.UtcNow).TotalDays));
        dto.Limits = new MyPlanLimitsDto
        {
            ListingLimit = limits.ListingLimit,
            Priority = limits.PriorityRank,
            Featured = limits.IsFeatured,
            JobsPosts = limits.JobsPosts,
        };

        if (PlanFeatures.HasListingSlots(feature))
        {
            var counts = await CountSlotsAsync(userId, feature);
            dto.Limits.Used = counts.Used;
            dto.Limits.Live = counts.Live;
            dto.PausedByPlanCount = counts.PlanPaused;
        }

        if (flag.Enabled)
        {
            var snapshot = await catalog.GetAsync();
            var trialPlan = snapshot.PlansOf(feature).Any(p => p.IsEnabled && !p.IsDefault && p.TrialDays > 0);
            dto.TrialAvailable = trialPlan && !await db.UserPlans.AnyAsync(u => u.UserId == userId && u.FeatureKey == feature);
        }

        dto.OpenPurchaseId = (await unitOfWork.PlanPurchases.GetOpenByUserFeatureAsync(userId, feature))?.Id;
        return dto;
    }

    private sealed record SlotCounts(int Used, int Live, int PlanPaused);

    // One grouped COUNT over ix_*_owner_count instead of three round trips.
    private async Task<SlotCounts> CountSlotsAsync(Guid userId, string kind)
    {
        if (kind == ListingKinds.Room)
        {
            var r = await db.RoomListings.AsNoTracking()
                .Where(l => l.UserId == userId && !l.IsDeleted)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Used = g.Count(l => l.LiveRequestStatus != GoLiveRequestStatuses.Rejected),
                    Live = g.Count(l => l.IsActive || l.LiveRequestStatus == GoLiveRequestStatuses.Pending),
                    Paused = g.Count(l => !l.IsActive && l.PauseReason == PauseReasons.Plan),
                })
                .FirstOrDefaultAsync();
            return r == null ? new SlotCounts(0, 0, 0) : new SlotCounts(r.Used, r.Live, r.Paused);
        }
        var p = await db.PlotListings.AsNoTracking()
            .Where(l => l.UserId == userId && !l.IsDeleted)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Used = g.Count(l => l.LiveRequestStatus != GoLiveRequestStatuses.Rejected),
                Live = g.Count(l => l.IsActive || l.LiveRequestStatus == GoLiveRequestStatuses.Pending),
                Paused = g.Count(l => !l.IsActive && l.PauseReason == PauseReasons.Plan),
            })
            .FirstOrDefaultAsync();
        return p == null ? new SlotCounts(0, 0, 0) : new SlotCounts(p.Used, p.Live, p.Paused);
    }

    public async Task<PurchaseStatusDto> GetStatusAsync(Guid userId, Guid purchaseId)
    {
        var p = await unitOfWork.PlanPurchases.GetByIdAsync(purchaseId);
        if (p == null || p.UserId != userId)
            throw PlanServiceException.NotFound(PlanErrorTypes.PurchaseNotFound, "Purchase not found.");
        return await ToStatusAsync(p);
    }

    public async Task<PurchaseStatusDto?> GetOpenAsync(Guid userId)
    {
        var p = await unitOfWork.PlanPurchases.GetLatestOpenByUserAsync(userId);
        return p == null ? null : await ToStatusAsync(p);
    }

    public async Task<List<PurchaseHistoryItemDto>> GetHistoryAsync(Guid userId, int limit)
    {
        var rows = await unitOfWork.PlanPurchases.GetHistoryAsync(userId, Math.Clamp(limit, 1, 50));
        return rows.Select(p => new PurchaseHistoryItemDto
        {
            Id = p.Id,
            PlanName = p.PlanNameSnapshot,
            CycleMonths = p.CycleMonths,
            PayableInr = p.PayableInr,
            Status = p.Status,
            CreatedAt = p.CreatedAt,
            GrantedAt = p.GrantedAt,
            RazorpayPaymentId = p.RazorpayPaymentId,
        }).ToList();
    }

    private async Task<PurchaseStatusDto> ToStatusAsync(PlanPurchase p)
    {
        DateTime? endsAt = null;
        if (p.Status == PurchaseStatuses.Granted && p.GrantedUserPlanId != null)
            endsAt = await db.UserPlans.AsNoTracking()
                .Where(u => u.Id == p.GrantedUserPlanId)
                .Select(u => (DateTime?)u.EndsAt)
                .FirstOrDefaultAsync();
        return new PurchaseStatusDto
        {
            PurchaseId = p.Id,
            Status = p.Status,
            PlanName = p.PlanNameSnapshot,
            GrantedDays = p.GrantedDays,
            EndsAt = endsAt,
            FailureReason = p.FailureReason,
            GrantBlockedReason = p.GrantBlockedReason,
        };
    }

    // ------------------------------------------------------------------ quote / order

    public async Task<QuoteDto> QuoteAsync(Guid userId, QuoteRequest request)
    {
        await RateLimitAsync($"plan:quote:{userId}", 30);
        var q = await BuildQuoteAsync(userId, request.PlanId, request.CycleMonths, request.CouponCode);
        return ToQuoteDto(q);
    }

    private static QuoteDto ToQuoteDto(QuoteCalc q) => new()
    {
        PlanId = q.Plan.Id,
        PlanName = q.Plan.Name,
        CycleMonths = q.Price.CycleMonths,
        Intent = q.Intent,
        ListPriceInr = q.ListPriceInr,
        DiscountInr = q.DiscountInr,
        CreditInr = q.CreditInr,
        PayableInr = q.PayableInr,
        GrantedDays = q.GrantedDays,
        RequiresPayment = q.PayableInr > 0,
        Coupon = q.Coupon == null ? null : new QuoteCouponDto { Code = q.Coupon.Code, Kind = q.Coupon.Kind, Value = q.Coupon.Value },
        AutoCoupon = q.AutoCoupon,
    };

    private async Task RateLimitAsync(string key, int max)
    {
        var rl = await rateLimiter.CheckAsync(key, max, TimeSpan.FromMinutes(10));
        if (!rl.IsAllowed) throw PlanServiceException.TooMany(rl.RetryAfter);
    }

    private async Task<UserInfo> GetUserAsync(Guid userId)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserInfo(u.Id, u.RoleId, u.CreatedAt))
            .FirstOrDefaultAsync();
        return user ?? throw PlanServiceException.NotFound(PlanErrorTypes.PlanNotFound, "User not found.");
    }

    // Shared gates for quote, order and trial: payment on, plan exists, role feature matches, tab on.
    private async Task<(CatalogPlan Plan, UserInfo User)> GatePlanAsync(Guid userId, Guid planId)
    {
        var flag = await payment.GetAsync();
        if (!flag.Enabled)
            throw PlanServiceException.Conflict(PlanErrorTypes.PaymentDisabled, "Plans are not available right now.");

        var snapshot = await catalog.GetAsync();
        var plan = snapshot.GetPlan(planId)
            ?? throw PlanServiceException.BadRequest(PlanErrorTypes.PlanNotFound, "Plan not found.");

        var user = await GetUserAsync(userId);
        if (UserRoles.ListingKindOf(user.RoleId) != plan.FeatureKey)
            throw PlanServiceException.Forbidden(PlanErrorTypes.RoleMismatch, "This plan is not for your role.");

        var tabs = await AppTabCache.GetAsync(unitOfWork, memoryCache);
        if (!TabState.IsKindActive(tabs, plan.FeatureKey))
            throw PlanServiceException.Forbidden(PlanErrorTypes.TabInactive, "This section is not available right now.");

        return (plan, user);
    }

    private async Task<QuoteCalc> BuildQuoteAsync(Guid userId, Guid planId, int cycleMonths, string? couponCode)
    {
        var (plan, user) = await GatePlanAsync(userId, planId);
        if (plan.IsDefault)
            throw PlanServiceException.BadRequest(PlanErrorTypes.PlanPriceNotFound, "The free plan cannot be bought.");
        if (!plan.IsEnabled)
            throw PlanServiceException.BadRequest(PlanErrorTypes.PlanDisabled, "This plan is no longer available.");
        var price = plan.Prices.FirstOrDefault(p => p.CycleMonths == cycleMonths && p.IsEnabled)
            ?? throw PlanServiceException.BadRequest(PlanErrorTypes.PlanPriceNotFound, "This billing cycle is not available.");

        var now = DateTime.UtcNow;
        var days = PlanCycles.DaysOf(cycleMonths);
        var active = await db.UserPlans.AsNoTracking()
            .Where(u => u.UserId == userId && u.FeatureKey == plan.FeatureKey
                && u.Status == UserPlanStatuses.Active && u.EndsAt > now)
            .Select(u => new { u.PlanId, u.Source, u.EndsAt, u.TotalPaidInr, u.TotalDays })
            .FirstOrDefaultAsync();

        string intent;
        var credit = 0;
        if (active == null)
        {
            intent = PurchaseIntents.New;
        }
        else if (active.PlanId == plan.Id && active.Source != UserPlanSources.Trial)
        {
            intent = PurchaseIntents.Renew;
            if ((active.EndsAt - now).TotalDays + days > PlanRules.MaxRemainingDays)
                throw PlanServiceException.Conflict(PlanErrorTypes.RenewLimitReached, "You can renew when less time is left on your plan.");
        }
        else
        {
            intent = PurchaseIntents.Switch;
            if (active.TotalPaidInr > 0 && active.TotalDays > 0)
            {
                var remaining = Math.Max(0, (active.EndsAt - now).TotalDays);
                credit = (int)Math.Floor(active.TotalPaidInr * remaining / active.TotalDays);
                credit = Math.Clamp(credit, 0, Math.Max(0, price.PriceInr - 1));
            }
        }

        var list = price.PriceInr;
        Coupon? coupon = null;
        var auto = false;
        // The user's own open order does not block its coupon: that order is released when replaced.
        Guid? openId = null;
        async Task<Guid?> OpenIdAsync() => openId ??= (await unitOfWork.PlanPurchases.GetOpenByUserFeatureAsync(userId, plan.FeatureKey))?.Id;
        if (!string.IsNullOrWhiteSpace(couponCode))
        {
            coupon = await unitOfWork.Coupons.GetByCodeAsync(NormalizeCode(couponCode));
            if (coupon == null || !await IsCouponUsableAsync(coupon, user, plan, list, intent, await OpenIdAsync()))
                throw PlanServiceException.BadRequest(PlanErrorTypes.CouponInvalid, CouponInvalidMessage);
        }
        else
        {
            var candidates = await db.Coupons.AsNoTracking()
                .Where(c => c.AutoApply && c.Status == CouponStatuses.Active
                    && (c.FeatureKey == plan.FeatureKey || c.FeatureKey == null))
                .OrderBy(c => c.FeatureKey == null)
                .ToListAsync();
            foreach (var c in candidates)
            {
                if (!await IsCouponUsableAsync(c, user, plan, list, intent, await OpenIdAsync())) continue;
                coupon = c;
                auto = true;
                break;
            }
        }

        var discount = coupon == null ? 0 : DiscountOf(coupon, list);
        var payable = list - discount - credit;
        var fullyDiscounted = coupon != null && discount >= list;
        payable = fullyDiscounted ? 0 : Math.Max(1, payable);

        return new QuoteCalc(plan, price, plan.FeatureKey, intent, list, discount, credit, payable, days, coupon, auto);
    }

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    private static int DiscountOf(Coupon c, int list) => c.Kind switch
    {
        CouponKinds.Percent => Math.Min(list * c.Value / 100, c.MaxDiscountInr ?? int.MaxValue),
        CouponKinds.Flat => Math.Min(Math.Max(0, c.Value), list),
        CouponKinds.FreePlan => list,
        _ => 0,
    };

    // Every failing reason maps to one generic CouponInvalid on the wire.
    private async Task<bool> IsCouponUsableAsync(
        Coupon c, UserInfo user, CatalogPlan plan, int listPrice, string intent, Guid? ignorePurchaseId)
    {
        var now = DateTime.UtcNow;
        if (c.Status != CouponStatuses.Active) return false;
        if (c.ValidFrom != null && c.ValidFrom > now) return false;
        if (c.ValidUntil != null && c.ValidUntil <= now) return false;
        if (c.MaxTotalRedemptions != null && c.RedemptionCount >= c.MaxTotalRedemptions) return false;
        if (c.FeatureKey != null && c.FeatureKey != plan.FeatureKey) return false;
        if (c.AllowedPlanIds is { Length: > 0 } && !c.AllowedPlanIds.Contains(plan.Id)) return false;
        if (c.MinPlanPriceInr != null && listPrice < c.MinPlanPriceInr) return false;
        if (c.Kind == CouponKinds.FreePlan && intent == PurchaseIntents.Switch) return false;
        if (!CouponKinds.All.Contains(c.Kind)) return false;
        if (c.NewUsersOnly && user.CreatedAt < now.AddDays(-PlanRules.NewUserWindowDays)) return false;

        if (c.FirstPurchaseOnly)
        {
            var bought = await db.PlanPurchases.AsNoTracking().AnyAsync(p => p.UserId == user.Id
                && p.Intent != PurchaseIntents.AdminGrant
                && (p.Status == PurchaseStatuses.Granted || p.Status == PurchaseStatuses.Paid || p.Status == PurchaseStatuses.Refunded));
            if (bought) return false;
        }

        var used = await db.CouponRedemptions.AsNoTracking().CountAsync(r => r.CouponId == c.Id && r.UserId == user.Id
            && (r.Status == CouponRedemptionStatuses.Reserved || r.Status == CouponRedemptionStatuses.Consumed)
            && (ignorePurchaseId == null || r.PurchaseId != ignorePurchaseId));
        return used < Math.Max(1, c.PerUserLimit);
    }

    public async Task<CreatePlanOrderDto> CreateOrderAsync(Guid userId, CreatePlanOrderRequest request)
    {
        await RateLimitAsync($"plan:create-order:{userId}", 10);

        var now = DateTime.UtcNow;
        var q = await BuildQuoteAsync(userId, request.PlanId, request.CycleMonths, request.CouponCode);
        var couponSnapshot = q.Coupon?.Code;

        var open = await unitOfWork.PlanPurchases.GetOpenByUserFeatureAsync(userId, q.FeatureKey);
        if (open != null)
        {
            var same = open.PlanId == q.Plan.Id && open.CycleMonths == q.Price.CycleMonths
                && open.CouponCodeSnapshot == couponSnapshot && open.PayableInr == q.PayableInr
                && open.ExpiresAt > now && open.RazorpayOrderId != null;
            if (same) return ToOrderDto(open, requiresPayment: true);
            await CloseOpenAsync(open.Id, PurchaseStatuses.Expired, "Replaced by a new order");
        }

        var purchase = new PlanPurchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FeatureKey = q.FeatureKey,
            PlanId = q.Plan.Id,
            PlanNameSnapshot = q.Plan.Name,
            CycleMonths = q.Price.CycleMonths,
            GrantedDays = q.GrantedDays,
            Intent = q.Intent,
            Provider = q.PayableInr > 0 ? PaymentProviders.Razorpay : PaymentProviders.None,
            Currency = "INR",
            ListPriceInr = q.ListPriceInr,
            DiscountInr = q.DiscountInr,
            CreditInr = q.CreditInr,
            PayableInr = q.PayableInr,
            CouponId = q.Coupon?.Id,
            CouponCodeSnapshot = couponSnapshot,
            Status = PurchaseStatuses.Created,
            CreatedAt = now,
            ExpiresAt = now + PlanRules.OpenOrderTtl,
        };

        GrantOutcome? granted = null;
        var tx = await db.Database.BeginTransactionAsync();
        try
        {
            if (q.PayableInr == 0)
                await LockOwnerForGrantAsync(userId, q.FeatureKey);
            db.PlanPurchases.Add(purchase);
            if (q.Coupon != null)
            {
                if (!await ReserveCouponAsync(q.Coupon.Id))
                    throw PlanServiceException.BadRequest(PlanErrorTypes.CouponInvalid, CouponInvalidMessage);
                db.CouponRedemptions.Add(new CouponRedemption
                {
                    Id = Guid.NewGuid(),
                    CouponId = q.Coupon.Id,
                    UserId = userId,
                    PurchaseId = purchase.Id,
                    DiscountInr = q.DiscountInr,
                    Status = CouponRedemptionStatuses.Reserved,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            await db.SaveChangesAsync();

            // Rs 0 order (FreePlan or a full discount): granted here, no gateway call.
            if (q.PayableInr == 0)
            {
                granted = await GrantInTxAsync(userId, q.FeatureKey, q.Plan.Id, q.GrantedDays, UserPlanSources.Prepaid,
                    paidInr: 0, creditInr: q.CreditInr, purchase, adminId: null, note: null);
            }
            await tx.CommitAsync();
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            var constraint = (ex.InnerException as PostgresException)?.ConstraintName;
            if (constraint == "ix_couponredemptions_coupon_user_active")
                throw PlanServiceException.BadRequest(PlanErrorTypes.CouponInvalid, CouponInvalidMessage);
            if (constraint == "ix_planpurchases_one_open")
            {
                var winner = await unitOfWork.PlanPurchases.GetOpenByUserFeatureAsync(userId, q.FeatureKey);
                if (winner != null && winner.PlanId == q.Plan.Id && winner.CycleMonths == q.Price.CycleMonths
                    && winner.CouponCodeSnapshot == couponSnapshot && winner.RazorpayOrderId != null)
                    return ToOrderDto(winner, requiresPayment: true);
                throw PlanServiceException.Conflict(PlanErrorTypes.PurchaseInProgress, "A payment is already in progress. Please try again.");
            }
            throw;
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

        if (granted != null)
        {
            await AfterGrantAsync(userId, q.FeatureKey, granted);
            return ToOrderDto(purchase, requiresPayment: false);
        }

        // External call strictly after the Created row is committed (receipt = purchase id).
        string orderId;
        try
        {
            var notes = new Dictionary<string, string>
            {
                ["purchase_id"] = purchase.Id.ToString(),
                ["user_id"] = userId.ToString(),
            };
            var (createdOrderId, amount) = await razorpay.CreateOrderAsync(q.PayableInr, purchase.Id.ToString("N"), notes);
            if (amount != q.PayableInr)
            {
                logger.LogError("Razorpay amount mismatch for purchase {PurchaseId}: expected {Expected}, got {Actual}", purchase.Id, q.PayableInr, amount);
                throw new InvalidOperationException("Payment amount mismatch.");
            }
            orderId = createdOrderId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Razorpay order creation failed for purchase {PurchaseId}", purchase.Id);
            await CloseOpenAsync(purchase.Id, PurchaseStatuses.Failed, "Order creation failed");
            throw PlanServiceException.Gateway("Payment gateway is unavailable. Please try again.");
        }

        if (!await unitOfWork.PlanPurchases.SetOrderIdIfCreatedAsync(purchase.Id, orderId))
            throw PlanServiceException.Conflict(PlanErrorTypes.PurchaseInProgress, "This order is no longer open. Please try again.");

        purchase.RazorpayOrderId = orderId;
        return ToOrderDto(purchase, requiresPayment: true);
    }

    private CreatePlanOrderDto ToOrderDto(PlanPurchase p, bool requiresPayment) => new()
    {
        PurchaseId = p.Id,
        Status = p.Status,
        RequiresPayment = requiresPayment,
        RazorpayOrderId = requiresPayment ? p.RazorpayOrderId : null,
        KeyId = requiresPayment ? razorpay.GetKeyId() : null,
        AmountPaise = p.PayableInr * 100,
        Currency = p.Currency,
        ExpiresAt = p.ExpiresAt,
        PlanName = p.PlanNameSnapshot,
        GrantedDays = p.GrantedDays,
    };

    // ------------------------------------------------------------------ coupon engine

    private async Task<bool> ReserveCouponAsync(Guid couponId)
        => await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Coupons" SET
                "RedemptionCount" = "RedemptionCount" + 1,
                "Status" = CASE WHEN "MaxTotalRedemptions" IS NOT NULL AND "RedemptionCount" + 1 >= "MaxTotalRedemptions"
                                THEN 'Exhausted' ELSE "Status" END,
                "UpdatedAt" = now()
            WHERE "Id" = {couponId} AND "Status" = 'Active'
              AND ("MaxTotalRedemptions" IS NULL OR "RedemptionCount" < "MaxTotalRedemptions")
            """) > 0;

    private async Task ReleaseCouponAsync(Guid purchaseId)
        => await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH r AS (
                UPDATE "CouponRedemptions" SET "Status" = 'Released', "UpdatedAt" = now()
                WHERE "PurchaseId" = {purchaseId} AND "Status" = 'Reserved'
                RETURNING "CouponId"
            )
            UPDATE "Coupons" c SET
                "RedemptionCount" = GREATEST(c."RedemptionCount" - 1, 0),
                "Status" = CASE WHEN c."Status" = 'Exhausted' THEN 'Active' ELSE c."Status" END,
                "UpdatedAt" = now()
            FROM r WHERE c."Id" = r."CouponId"
            """);

    // Reserved -> Consumed; a coupon released by an expired order is re-consumed on a late capture
    // (money was taken) unless the user already holds another live redemption of it.
    private async Task ConsumeCouponAsync(Guid purchaseId)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "CouponRedemptions" SET "Status" = 'Consumed', "UpdatedAt" = now()
            WHERE "PurchaseId" = {purchaseId} AND "Status" = 'Reserved'
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH r AS (
                UPDATE "CouponRedemptions" cr SET "Status" = 'Consumed', "UpdatedAt" = now()
                WHERE cr."PurchaseId" = {purchaseId} AND cr."Status" = 'Released'
                  AND NOT EXISTS (
                    SELECT 1 FROM "CouponRedemptions" o
                    WHERE o."CouponId" = cr."CouponId" AND o."UserId" = cr."UserId"
                      AND o."Status" IN ('Reserved', 'Consumed'))
                RETURNING cr."CouponId"
            )
            UPDATE "Coupons" c SET "RedemptionCount" = c."RedemptionCount" + 1, "UpdatedAt" = now()
            FROM r WHERE c."Id" = r."CouponId"
            """);
    }

    // Open (Created/Pending) -> Expired/Failed, coupon released, in one short transaction.
    private async Task<bool> CloseOpenAsync(Guid purchaseId, string status, string reason, string? paymentId = null)
    {
        var tx = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync() : null;
        try
        {
            var changed = await db.PlanPurchases
                .Where(p => p.Id == purchaseId && (p.Status == PurchaseStatuses.Created || p.Status == PurchaseStatuses.Pending))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, status)
                    .SetProperty(p => p.FailureReason, reason)
                    .SetProperty(p => p.FailedPaymentId, p => paymentId ?? p.FailedPaymentId)) > 0;
            if (changed) await ReleaseCouponAsync(purchaseId);
            if (tx != null) await tx.CommitAsync();
            return changed;
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync();
            throw;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ settle

    public async Task<(PurchaseStatusDto Status, NotificationEvent? Notification)> VerifyAsync(
        Guid userId, Guid purchaseId, VerifyPaymentRequest request)
    {
        var p = await unitOfWork.PlanPurchases.GetByIdAsync(purchaseId);
        if (p == null || p.UserId != userId)
            throw PlanServiceException.NotFound(PlanErrorTypes.PurchaseNotFound, "Purchase not found.");
        if (p.Status is PurchaseStatuses.Granted or PurchaseStatuses.Refunded)
            return (await ToStatusAsync(p), null);
        if (p.RazorpayOrderId == null || p.RazorpayOrderId != request.RazorpayOrderId
            || !razorpay.VerifyPaymentSignature(request.RazorpayOrderId, request.RazorpayPaymentId, request.RazorpaySignature))
            throw PlanServiceException.BadRequest(PlanErrorTypes.SignatureInvalid, "Payment could not be verified.");

        IReadOnlyList<RazorpayPaymentAttempt> attempts;
        try
        {
            attempts = await razorpay.FetchPaymentsForOrderAsync(p.RazorpayOrderId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Verify: could not fetch payments for order {OrderId}; the reconciler will settle it", p.RazorpayOrderId);
            await unitOfWork.PlanPurchases.MarkPendingIfCreatedAsync(p.Id);
            return (await ToStatusAsync((await unitOfWork.PlanPurchases.GetByIdAsync(p.Id))!), null);
        }

        var captured = attempts.FirstOrDefault(a => a.Id == request.RazorpayPaymentId && a.Status == Captured)
            ?? attempts.FirstOrDefault(a => a.Status == Captured);
        NotificationEvent? notification = null;
        if (captured != null)
        {
            var result = await ApplyCapturedAsync(p.Id, captured.Id, captured.AmountPaise, captured.Currency, PurchaseSources.Verify);
            notification = result.Notification;
        }
        else if (attempts.Any(a => StillResolving.Contains(a.Status)))
        {
            await unitOfWork.PlanPurchases.MarkPendingIfCreatedAsync(p.Id);
        }

        var current = await unitOfWork.PlanPurchases.GetByIdAsync(p.Id);
        return (await ToStatusAsync(current!), notification);
    }

    public async Task<CaptureResult> ApplyCapturedAsync(Guid purchaseId, string paymentId, long amountPaise, string currency, string source)
    {
        var ownerId = await db.PlanPurchases.AsNoTracking()
            .Where(p => p.Id == purchaseId)
            .Select(p => p.UserId)
            .FirstOrDefaultAsync();

        var tx = await db.Database.BeginTransactionAsync();
        GrantOutcome? granted = null;
        PlanPurchase? purchase;
        CaptureOutcome outcome;
        try
        {
            // Owner lock before the purchase lock: serialises with role changes and keeps one lock order.
            if (ownerId != null) await slots.LockOwnerAsync(ownerId.Value);
            purchase = await LockPurchaseAsync(purchaseId);
            if (purchase == null)
            {
                await tx.RollbackAsync();
                return new CaptureResult(CaptureOutcome.NotFound, null, null);
            }
            if (purchase.Status is PurchaseStatuses.Granted or PurchaseStatuses.Refunded
                || (purchase.Status == PurchaseStatuses.Paid && purchase.GrantBlockedReason != null))
            {
                await tx.RollbackAsync();
                return new CaptureResult(CaptureOutcome.AlreadyProcessed, purchase, null);
            }

            var now = DateTime.UtcNow;
            purchase.RazorpayPaymentId = paymentId;
            purchase.CapturedAmountPaise = amountPaise;
            purchase.PaidAt ??= now;

            var featureNow = purchase.UserId == null ? null : await db.Users.AsNoTracking()
                .Where(u => u.Id == purchase.UserId)
                .Select(u => (Guid?)u.RoleId)
                .FirstOrDefaultAsync();

            if (amountPaise != (long)purchase.PayableInr * 100 || !string.Equals(currency, purchase.Currency, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("Purchase {PurchaseId} captured {Amount} {Currency}, expected {Expected} {ExpectedCurrency} ({Source})",
                    purchase.Id, amountPaise, currency, purchase.PayableInr * 100, purchase.Currency, source);
                purchase.Status = PurchaseStatuses.Paid;
                purchase.GrantBlockedReason = GrantBlockedReasons.AmountMismatch;
                if (purchase.CouponId != null) await ReleaseCouponAsync(purchase.Id);
                outcome = CaptureOutcome.Blocked;
            }
            else if (featureNow == null || purchase.UserId is not { } buyerId || buyerId != ownerId
                || UserRoles.ListingKindOf(featureNow.Value) != purchase.FeatureKey)
            {
                // Money taken but the role moved on: the reconciler refunds it in full.
                purchase.Status = PurchaseStatuses.Paid;
                purchase.GrantBlockedReason = GrantBlockedReasons.RoleMismatch;
                if (purchase.CouponId != null) await ReleaseCouponAsync(purchase.Id);
                outcome = CaptureOutcome.Blocked;
            }
            else if (purchase.CreditInr > 0 && !await SwitchCreditStillValidAsync(purchase, buyerId, now))
            {
                // The switch credit was priced off a plan state that is gone: refund, never over-credit.
                logger.LogWarning("Purchase {PurchaseId} switch credit {Credit} no longer valid at settlement ({Source})",
                    purchase.Id, purchase.CreditInr, source);
                purchase.Status = PurchaseStatuses.Paid;
                purchase.GrantBlockedReason = GrantBlockedReasons.QuoteExpired;
                if (purchase.CouponId != null) await ReleaseCouponAsync(purchase.Id);
                outcome = CaptureOutcome.Blocked;
            }
            else
            {
                // Tab off, plan disabled or order expired do not block: money taken means grant.
                granted = await GrantInTxAsync(buyerId, purchase.FeatureKey, purchase.PlanId, purchase.GrantedDays,
                    UserPlanSources.Prepaid, purchase.PayableInr, purchase.CreditInr, purchase, adminId: null, note: null);
                outcome = CaptureOutcome.Granted;
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();
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

        if (granted != null)
            await AfterGrantAsync(purchase.UserId!.Value, purchase.FeatureKey, granted);
        logger.LogInformation("Purchase {PurchaseId} settled via {Source}: {Outcome}", purchase.Id, source, outcome);
        return new CaptureResult(outcome, purchase, granted?.Notification);
    }

    // Caller holds the owner lock. Recomputes the quote's switch credit from the plan the owner holds now,
    // as of the order's creation: valid only inside the order window and if it still covers CreditInr.
    private async Task<bool> SwitchCreditStillValidAsync(PlanPurchase purchase, Guid userId, DateTime now)
    {
        if (now > purchase.ExpiresAt) return false;
        var active = await db.UserPlans.AsNoTracking()
            .Where(u => u.UserId == userId && u.FeatureKey == purchase.FeatureKey
                && u.Status == UserPlanStatuses.Active && u.EndsAt > now)
            .Select(u => new { u.PlanId, u.EndsAt, u.TotalPaidInr, u.TotalDays })
            .FirstOrDefaultAsync();
        if (active == null || active.PlanId == purchase.PlanId || active.TotalPaidInr <= 0 || active.TotalDays <= 0)
            return false;
        var remaining = Math.Max(0, (active.EndsAt - purchase.CreatedAt).TotalDays);
        var credit = (int)Math.Floor(active.TotalPaidInr * remaining / active.TotalDays);
        credit = Math.Clamp(credit, 0, Math.Max(0, purchase.ListPriceInr - 1));
        return credit >= purchase.CreditInr;
    }

    // Owner lock, then the role re-read under it, so a grant and a role change never interleave.
    private async Task LockOwnerForGrantAsync(Guid userId, string featureKey)
    {
        await slots.LockOwnerAsync(userId);
        var roleId = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (Guid?)u.RoleId)
            .FirstOrDefaultAsync();
        if (roleId == null || UserRoles.ListingKindOf(roleId.Value) != featureKey)
            throw PlanServiceException.Forbidden(PlanErrorTypes.RoleMismatch, "This plan is not for the user's role.");
    }

    // FOR UPDATE on the purchase row, then a fresh tracked load (any stale tracked copy is detached first).
    private async Task<PlanPurchase?> LockPurchaseAsync(Guid purchaseId)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"PlanPurchases\" WHERE \"Id\" = {purchaseId} FOR UPDATE");
        foreach (var entry in db.ChangeTracker.Entries<PlanPurchase>().Where(e => e.Entity.Id == purchaseId).ToList())
            entry.State = EntityState.Detached;
        return await db.PlanPurchases.FirstOrDefaultAsync(p => p.Id == purchaseId);
    }

    // Caller holds the transaction and the owner lock. Renew extends, a different plan (or a trial) is
    // switched in place, no active row inserts a new one. Listing effects run in the same transaction.
    private async Task<GrantOutcome> GrantInTxAsync(
        Guid userId, string featureKey, Guid planId, int days, string source, int paidInr, int creditInr,
        PlanPurchase? purchase, Guid? adminId, string? note)
    {
        var now = DateTime.UtcNow;
        var snapshot = await catalog.GetAsync();
        var planName = snapshot.GetPlan(planId)?.Name ?? purchase?.PlanNameSnapshot ?? "Plan";

        await db.UserPlans
            .Where(u => u.UserId == userId && u.FeatureKey == featureKey && u.Status == UserPlanStatuses.Active && u.EndsAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Status, UserPlanStatuses.Expired)
                .SetProperty(u => u.UpdatedAt, now));

        foreach (var entry in db.ChangeTracker.Entries<UserPlan>().Where(e => e.Entity.UserId == userId).ToList())
            entry.State = EntityState.Detached;
        var active = await db.UserPlans.FirstOrDefaultAsync(u =>
            u.UserId == userId && u.FeatureKey == featureKey && u.Status == UserPlanStatuses.Active);

        var cap = now.AddDays(PlanRules.MaxRemainingDays);
        if (active != null && active.PlanId == planId && active.Source != UserPlanSources.Trial)
        {
            var ends = active.EndsAt.AddDays(days);
            active.EndsAt = ends > cap ? cap : ends;
            active.TotalPaidInr += paidInr;
            active.TotalDays += days;
            if (source == UserPlanSources.Prepaid) active.Source = source;
        }
        else if (active != null)
        {
            active.PlanId = planId;
            active.Source = source;
            active.StartsAt = now;
            active.EndsAt = now.AddDays(days);
            active.TotalPaidInr = paidInr + creditInr;
            active.TotalDays = days;
        }
        else
        {
            active = new UserPlan
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                FeatureKey = featureKey,
                PlanId = planId,
                Source = source,
                Status = UserPlanStatuses.Active,
                StartsAt = now,
                EndsAt = now.AddDays(days),
                TotalPaidInr = paidInr,
                TotalDays = days,
                CreatedAt = now,
            };
            db.UserPlans.Add(active);
        }
        active.ReminderSentAt = null;
        active.UpdatedAt = now;
        if (adminId != null) active.GrantedByAdminId = adminId;
        if (note != null) active.Note = note.Length > 300 ? note[..300] : note;
        await db.SaveChangesAsync();

        if (purchase != null)
        {
            purchase.Status = PurchaseStatuses.Granted;
            purchase.GrantedAt = now;
            purchase.GrantedUserPlanId = active.Id;
            purchase.GrantBlockedReason = null;
            purchase.FailureReason = null;
            await db.SaveChangesAsync();
            if (purchase.CouponId != null) await ConsumeCouponAsync(purchase.Id);
        }

        await slots.ApplyGrantEffectsAsync(userId, featureKey);

        var notification = PlanNotifications.Activated(userId, planName, active.EndsAt);
        db.NotificationEvents.Add(notification);
        await db.SaveChangesAsync();
        return new GrantOutcome(active, notification);
    }

    private async Task AfterGrantAsync(Guid userId, string featureKey, GrantOutcome granted)
    {
        await PlanNotifications.PublishAsync(publisher, granted.Notification, logger);
        await cacheInvalidator.BustForUserAsync(userId, featureKey);
    }

    public async Task<bool> MarkFailedIfOpenAsync(Guid purchaseId, string? paymentId, string reason)
        => await CloseOpenAsync(purchaseId, PurchaseStatuses.Failed, Truncate(reason, 300), paymentId);

    public async Task<bool> MarkRefundedAsync(string paymentId, string? refundId)
    {
        var p = await unitOfWork.PlanPurchases.GetByPaymentIdAsync(paymentId);
        if (p == null || p.Status == PurchaseStatuses.Refunded) return false;
        if (p.Status is not (PurchaseStatuses.Granted or PurchaseStatuses.Paid)) return false;
        return await ApplyRefundAsync(p.Id, refundId, "Refunded") != null;
    }

    // Purchase -> Refunded and its entitlement taken back: the latest grant on the row shortens EndsAt by
    // GrantedDays for Renew and cancels the plan otherwise; an older one only takes back its own share, so
    // later grants on the row survive (cancel only if nothing is left). Coupon is not restored.
    // Returns the feature key when effects ran.
    private async Task<string?> ApplyRefundAsync(Guid purchaseId, string? refundId, string note)
    {
        var ownerId = await db.PlanPurchases.AsNoTracking()
            .Where(p => p.Id == purchaseId)
            .Select(p => p.UserId)
            .FirstOrDefaultAsync();

        var tx = await db.Database.BeginTransactionAsync();
        Guid? userId = null;
        string? feature = null;
        try
        {
            if (ownerId != null) await slots.LockOwnerAsync(ownerId.Value);
            var purchase = await LockPurchaseAsync(purchaseId);
            if (purchase == null || purchase.Status == PurchaseStatuses.Refunded)
            {
                await tx.RollbackAsync();
                return null;
            }
            var wasGranted = purchase.Status == PurchaseStatuses.Granted;
            purchase.Status = PurchaseStatuses.Refunded;
            purchase.RefundedAt = DateTime.UtcNow;
            purchase.RazorpayRefundId ??= refundId;
            await db.SaveChangesAsync();

            if (wasGranted && purchase.UserId != null && purchase.UserId == ownerId && purchase.GrantedUserPlanId != null)
            {
                userId = purchase.UserId.Value;
                feature = purchase.FeatureKey;
                foreach (var entry in db.ChangeTracker.Entries<UserPlan>().Where(e => e.Entity.Id == purchase.GrantedUserPlanId).ToList())
                    entry.State = EntityState.Detached;
                var plan = await db.UserPlans.FirstOrDefaultAsync(u => u.Id == purchase.GrantedUserPlanId && u.Status == UserPlanStatuses.Active);
                if (plan != null)
                {
                    var now = DateTime.UtcNow;
                    // Later grants on the same row (renewals, admin days, Rs 0 coupons, a later switch) stay
                    // with the owner: only this purchase's own share is taken back, never the whole row.
                    var later = await db.PlanPurchases.AsNoTracking()
                        .Where(o => o.Id != purchase.Id
                            && o.GrantedUserPlanId == purchase.GrantedUserPlanId
                            && o.Status == PurchaseStatuses.Granted
                            && (o.GrantedAt > purchase.GrantedAt || (o.GrantedAt == purchase.GrantedAt && o.CreatedAt > purchase.CreatedAt)))
                        .OrderBy(o => o.GrantedAt).ThenBy(o => o.CreatedAt)
                        .Select(o => new { o.PlanId, o.GrantedDays, o.PayableInr, o.CreditInr })
                        .ToListAsync();

                    int takeBackDays, takeBackInr;
                    if (later.Count == 0)
                    {
                        takeBackDays = purchase.Intent == PurchaseIntents.Renew ? purchase.GrantedDays : -1;
                        takeBackInr = purchase.PayableInr;
                    }
                    else
                    {
                        // A later plan change reset the row: this purchase survives only as that switch's credit.
                        var change = later.FirstOrDefault(o => o.PlanId != purchase.PlanId);
                        if (change == null)
                        {
                            takeBackDays = purchase.GrantedDays;
                            takeBackInr = purchase.PayableInr;
                        }
                        else if (change.CreditInr <= 0)
                        {
                            takeBackDays = 0;
                            takeBackInr = 0;
                        }
                        else
                        {
                            takeBackDays = Math.Min(purchase.GrantedDays,
                                (int)Math.Ceiling((double)change.GrantedDays * change.CreditInr / (change.PayableInr + change.CreditInr)));
                            takeBackInr = Math.Min(purchase.PayableInr, change.CreditInr);
                        }
                    }

                    var shortened = takeBackDays < 0 ? now : plan.EndsAt.AddDays(-takeBackDays);
                    if (takeBackDays == 0)
                    {
                        logger.LogInformation("Refund of purchase {PurchaseId}: its days were already replaced on plan {UserPlanId}",
                            purchase.Id, plan.Id);
                    }
                    else if (takeBackDays > 0 && shortened > now && shortened > plan.StartsAt)
                    {
                        plan.EndsAt = shortened;
                        plan.TotalPaidInr = Math.Max(0, plan.TotalPaidInr - takeBackInr);
                        plan.TotalDays = Math.Max(1, plan.TotalDays - takeBackDays);
                        plan.UpdatedAt = now;
                        await db.SaveChangesAsync();
                        await slots.ApplyGrantEffectsAsync(userId.Value, feature);
                    }
                    else
                    {
                        Cancel(plan, now, note);
                        await db.SaveChangesAsync();
                        await slots.ExpireEffectsAsync(userId.Value, feature);
                    }
                }
            }
            await tx.CommitAsync();
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

        if (userId != null && feature != null)
            await cacheInvalidator.BustForUserAsync(userId.Value, feature);
        return feature ?? string.Empty;
    }

    private static void Cancel(UserPlan plan, DateTime now, string? note)
    {
        plan.Status = UserPlanStatuses.Cancelled;
        plan.EndsAt = plan.StartsAt < now ? now : plan.StartsAt.AddSeconds(1);
        plan.UpdatedAt = now;
        if (note != null) plan.Note = Truncate(note, 300);
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;

    // ------------------------------------------------------------------ webhook / reconcile

    public async Task<WebhookOutcome> ProcessWebhookAsync(string rawBody, string? eventIdHeader)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(rawBody);
        }
        catch (JsonException)
        {
            return WebhookOutcome.Invalid;
        }

        var eventType = Str(root, "event") ?? "unknown";
        var paymentEntity = Entity(root, "payment");
        var orderEntity = Entity(root, "order");
        var refundEntity = Entity(root, "refund");

        var paymentId = paymentEntity is { } pe ? Str(pe, "id") : null;
        var orderId = (paymentEntity is { } pe2 ? Str(pe2, "order_id") : null) ?? (orderEntity is { } oe ? Str(oe, "id") : null);
        var refundId = refundEntity is { } re ? Str(re, "id") : null;
        var refundPaymentId = refundEntity is { } re2 ? Str(re2, "payment_id") : null;

        var eventId = !string.IsNullOrWhiteSpace(eventIdHeader)
            ? eventIdHeader.Trim()
            : Sha256($"{eventType}|{paymentId ?? refundPaymentId}|{refundId}|{orderId}");
        if (eventId.Length > 128) eventId = Sha256(eventId);

        var inserted = await unitOfWork.PaymentEvents.TryInsertAsync(new PaymentEvent
        {
            EventId = eventId,
            EventType = Truncate(eventType, 64),
            RazorpayOrderId = orderId,
            RazorpayPaymentId = paymentId ?? refundPaymentId,
            RawBody = rawBody,
        });
        if (!inserted)
        {
            // A delivery whose first attempt failed mid-way (ProcessedAt null) is processed again; every step is idempotent.
            var processed = await db.PaymentEvents.AsNoTracking()
                .Where(e => e.EventId == eventId)
                .Select(e => e.ProcessedAt)
                .FirstOrDefaultAsync();
            if (processed != null) return WebhookOutcome.Duplicate;
        }

        WebhookOutcome outcome;
        switch (eventType)
        {
            case "payment.captured":
            case "order.paid":
            {
                var purchase = await FindPurchaseAsync(orderId, orderEntity);
                if (purchase == null || paymentId == null)
                {
                    logger.LogWarning("Webhook {EventType} for unknown order {OrderId}", eventType, orderId);
                    outcome = WebhookOutcome.UnknownOrder;
                    break;
                }
                var amount = paymentEntity is { } p && p.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt64() : 0;
                var currency = paymentEntity is { } p2 ? Str(p2, "currency") ?? "INR" : "INR";
                await ApplyCapturedAsync(purchase.Id, paymentId, amount, currency, PurchaseSources.Webhook);
                outcome = WebhookOutcome.Processed;
                break;
            }
            case "payment.failed":
            {
                var purchase = await FindPurchaseAsync(orderId, orderEntity);
                if (purchase == null)
                {
                    outcome = WebhookOutcome.UnknownOrder;
                    break;
                }
                var reason = paymentEntity is { } p ? Str(p, "error_description") ?? "Payment failed" : "Payment failed";
                await MarkFailedIfOpenAsync(purchase.Id, paymentId, reason);
                outcome = WebhookOutcome.Processed;
                break;
            }
            case "refund.processed":
            {
                if (refundPaymentId == null)
                {
                    outcome = WebhookOutcome.Ignored;
                    break;
                }
                var known = await unitOfWork.PlanPurchases.GetByPaymentIdAsync(refundPaymentId);
                if (known == null)
                {
                    outcome = WebhookOutcome.UnknownOrder;
                    break;
                }
                var refundAmount = refundEntity is { } re3 && re3.TryGetProperty("amount", out var ra) && ra.ValueKind == JsonValueKind.Number ? ra.GetInt64() : 0;
                if (refundAmount < (known.CapturedAmountPaise ?? (long)known.PayableInr * 100))
                {
                    logger.LogWarning("Partial refund {RefundId} of {Amount} on purchase {PurchaseId}; plan kept", refundId, refundAmount, known.Id);
                    outcome = WebhookOutcome.PartialRefund;
                    break;
                }
                await MarkRefundedAsync(refundPaymentId, refundId);
                outcome = WebhookOutcome.Processed;
                break;
            }
            default:
                outcome = WebhookOutcome.Ignored;
                break;
        }

        await unitOfWork.PaymentEvents.SetOutcomeAsync(eventId, outcome.ToString());
        return outcome;
    }

    // By order id; falls back to the order receipt (= purchase id) and backfills a missing order id.
    private async Task<PlanPurchase?> FindPurchaseAsync(string? orderId, JsonElement? orderEntity)
    {
        if (orderId != null)
        {
            var byOrder = await unitOfWork.PlanPurchases.GetByOrderIdAsync(orderId);
            if (byOrder != null) return byOrder;
        }
        var receipt = orderEntity is { } oe ? Str(oe, "receipt") : null;
        if (receipt == null || !Guid.TryParse(receipt, out var purchaseId)) return null;
        var byId = await unitOfWork.PlanPurchases.GetByIdAsync(purchaseId);
        if (byId != null && byId.RazorpayOrderId == null && orderId != null)
        {
            await db.PlanPurchases
                .Where(p => p.Id == purchaseId && p.RazorpayOrderId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.RazorpayOrderId, orderId));
            byId.RazorpayOrderId = orderId;
        }
        return byId;
    }

    private static JsonElement? Entity(JsonElement root, string name)
        => root.TryGetProperty("payload", out var payload)
           && payload.TryGetProperty(name, out var wrapper)
           && wrapper.TryGetProperty("entity", out var entity)
            ? entity
            : null;

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async Task<ReconcileOutcome> ReconcileOrderAsync(Guid purchaseId, string source)
    {
        var p = await unitOfWork.PlanPurchases.GetByIdAsync(purchaseId);
        if (p == null) return ReconcileOutcome.NotFound;
        if (p.Status is PurchaseStatuses.Granted or PurchaseStatuses.Refunded) return ReconcileOutcome.AlreadySettled;

        if (p.Status == PurchaseStatuses.Paid && p.RazorpayPaymentId != null
            && p.GrantBlockedReason is GrantBlockedReasons.RoleMismatch or GrantBlockedReasons.QuoteExpired)
        {
            try
            {
                var refundId = await razorpay.RefundAsync(p.RazorpayPaymentId, await RefundablePaiseAsync(p), $"rf_{p.Id:N}");
                await ApplyRefundAsync(p.Id, refundId, p.GrantBlockedReason == GrantBlockedReasons.RoleMismatch
                    ? "Auto refund: role changed" : "Auto refund: switch quote expired");
                return ReconcileOutcome.Refunded;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto refund failed for purchase {PurchaseId}", p.Id);
                return ReconcileOutcome.Inconclusive;
            }
        }
        if (p.Status == PurchaseStatuses.Paid && p.GrantBlockedReason != null) return ReconcileOutcome.AlreadySettled;

        if (p.RazorpayOrderId == null)
        {
            if (p.Status is PurchaseStatuses.Created or PurchaseStatuses.Pending && p.ExpiresAt <= DateTime.UtcNow)
                await CloseOpenAsync(p.Id, PurchaseStatuses.Expired, "Order expired");
            return ReconcileOutcome.NotPaid;
        }

        IReadOnlyList<RazorpayPaymentAttempt> attempts;
        try
        {
            attempts = await razorpay.FetchPaymentsForOrderAsync(p.RazorpayOrderId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reconcile: could not fetch payments for order {OrderId}", p.RazorpayOrderId);
            return ReconcileOutcome.Inconclusive;
        }

        var captured = attempts.FirstOrDefault(a => a.Status == Captured);
        if (captured != null)
        {
            var result = await ApplyCapturedAsync(p.Id, captured.Id, captured.AmountPaise, captured.Currency, source);
            return result.Outcome == CaptureOutcome.Granted ? ReconcileOutcome.Granted : ReconcileOutcome.AlreadySettled;
        }
        if (attempts.Any(a => StillResolving.Contains(a.Status)))
        {
            await unitOfWork.PlanPurchases.MarkPendingIfCreatedAsync(p.Id);
            return ReconcileOutcome.Inconclusive;
        }

        if (p.Status is PurchaseStatuses.Created or PurchaseStatuses.Pending && p.ExpiresAt <= DateTime.UtcNow)
            await CloseOpenAsync(p.Id, PurchaseStatuses.Expired, "Order expired");
        return ReconcileOutcome.NotPaid;
    }

    public async Task<int> ExpireStaleOpenAsync(int batchSize)
    {
        var now = DateTime.UtcNow;
        var ids = await db.PlanPurchases.AsNoTracking()
            .Where(p => (p.Status == PurchaseStatuses.Created || p.Status == PurchaseStatuses.Pending) && p.ExpiresAt <= now)
            .OrderBy(p => p.CreatedAt)
            .Take(Math.Clamp(batchSize, 1, 500))
            .Select(p => p.Id)
            .ToListAsync();
        var expired = 0;
        foreach (var id in ids)
            if (await CloseOpenAsync(id, PurchaseStatuses.Expired, "Order expired")) expired++;
        return expired;
    }

    // ------------------------------------------------------------------ trial / admin

    public async Task<MyPlanDto> StartTrialAsync(Guid userId, Guid planId)
    {
        var (plan, _) = await GatePlanAsync(userId, planId);
        if (plan.IsDefault || !plan.IsEnabled || plan.TrialDays <= 0)
            throw PlanServiceException.Conflict(PlanErrorTypes.TrialNotAvailable, "Free trial is not available.");

        GrantOutcome granted;
        var tx = await db.Database.BeginTransactionAsync();
        try
        {
            await LockOwnerForGrantAsync(userId, plan.FeatureKey);
            if (await db.UserPlans.AnyAsync(u => u.UserId == userId && u.FeatureKey == plan.FeatureKey))
                throw PlanServiceException.Conflict(PlanErrorTypes.TrialNotAvailable, "Free trial is not available.");
            granted = await GrantInTxAsync(userId, plan.FeatureKey, plan.Id, plan.TrialDays, UserPlanSources.Trial,
                paidInr: 0, creditInr: 0, purchase: null, adminId: null, note: "Trial");
            await tx.CommitAsync();
        }
        catch (DbUpdateException ex) when ((ex.InnerException as PostgresException)?.ConstraintName is "ix_userplans_trial_once" or "ix_userplans_active_per_feature")
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            throw PlanServiceException.Conflict(PlanErrorTypes.TrialNotAvailable, "Free trial is not available.");
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

        await AfterGrantAsync(userId, plan.FeatureKey, granted);
        return await GetMyPlanAsync(userId);
    }

    public async Task<AdminGrantResult> AdminGrantAsync(Guid adminId, Guid userId, AdminGrantPlanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 64)
            throw PlanServiceException.BadRequest(PlanErrorTypes.InvalidRequest, "A valid idempotency key is required.");
        if (request.Days < 1 || request.Days > PlanRules.MaxGrantDays)
            throw PlanServiceException.BadRequest(PlanErrorTypes.InvalidRequest, $"Days must be between 1 and {PlanRules.MaxGrantDays}.");

        var replay = await unitOfWork.PlanPurchases.GetByIdempotencyKeyAsync(request.IdempotencyKey);
        if (replay != null)
        {
            if (replay.UserId != userId)
                throw PlanServiceException.Conflict(PlanErrorTypes.InvalidRequest, "This idempotency key was used for another user.");
            return new AdminGrantResult(await ToAdminDtoAsync(replay), null, true);
        }

        var snapshot = await catalog.GetAsync();
        var plan = snapshot.GetPlan(request.PlanId);
        if (plan == null || plan.IsDefault)
            throw PlanServiceException.BadRequest(PlanErrorTypes.PlanNotFound, "Plan not found.");
        var user = await GetUserAsync(userId);
        if (UserRoles.ListingKindOf(user.RoleId) != plan.FeatureKey)
            throw PlanServiceException.Forbidden(PlanErrorTypes.RoleMismatch, "This plan is not for the user's role.");

        var now = DateTime.UtcNow;
        var purchase = new PlanPurchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FeatureKey = plan.FeatureKey,
            PlanId = plan.Id,
            PlanNameSnapshot = plan.Name,
            CycleMonths = null,
            GrantedDays = request.Days,
            Intent = PurchaseIntents.AdminGrant,
            Provider = PaymentProviders.None,
            Currency = "INR",
            Status = PurchaseStatuses.Granted,
            ClientIdempotencyKey = request.IdempotencyKey,
            GrantedByAdminId = adminId,
            CreatedAt = now,
            ExpiresAt = now,
            FailureReason = null,
        };

        GrantOutcome granted;
        var tx = await db.Database.BeginTransactionAsync();
        try
        {
            await LockOwnerForGrantAsync(userId, plan.FeatureKey);
            db.PlanPurchases.Add(purchase);
            await db.SaveChangesAsync();
            granted = await GrantInTxAsync(userId, plan.FeatureKey, plan.Id, request.Days, UserPlanSources.AdminGrant,
                paidInr: 0, creditInr: 0, purchase, adminId, Truncate(request.Reason ?? string.Empty, 300));
            await tx.CommitAsync();
        }
        catch (DbUpdateException ex) when ((ex.InnerException as PostgresException)?.ConstraintName == "ix_planpurchases_idempotency")
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            var winner = await unitOfWork.PlanPurchases.GetByIdempotencyKeyAsync(request.IdempotencyKey);
            return new AdminGrantResult(await ToAdminDtoAsync(winner!), null, true);
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

        await AfterGrantAsync(userId, plan.FeatureKey, granted);
        return new AdminGrantResult(await ToAdminDtoAsync(purchase), granted.Notification, false);
    }

    public async Task AdminRevokeAsync(Guid adminId, Guid userId, AdminRevokePlanRequest request)
    {
        string feature;
        var tx = await db.Database.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(userId);
            var plan = await db.UserPlans.FirstOrDefaultAsync(u =>
                u.Id == request.UserPlanId && u.UserId == userId && u.Status == UserPlanStatuses.Active);
            if (plan == null)
                throw PlanServiceException.NotFound(PlanErrorTypes.UserPlanNotFound, "No active plan to revoke.");
            feature = plan.FeatureKey;
            Cancel(plan, DateTime.UtcNow, $"Revoked: {request.Reason}");
            await db.SaveChangesAsync();
            await slots.ExpireEffectsAsync(userId, feature);
            await tx.CommitAsync();
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
        logger.LogInformation("Admin {AdminId} revoked plan {UserPlanId} of user {UserId}", adminId, request.UserPlanId, userId);
        await cacheInvalidator.BustForUserAsync(userId, feature);
    }

    public async Task<AdminPurchaseDto> AdminRefundAsync(Guid adminId, Guid purchaseId, string reason)
    {
        var p = await unitOfWork.PlanPurchases.GetByIdAsync(purchaseId)
            ?? throw PlanServiceException.NotFound(PlanErrorTypes.PurchaseNotFound, "Purchase not found.");
        if (p.Status == PurchaseStatuses.Refunded) return await ToAdminDtoAsync(p);
        if (p.Status is not (PurchaseStatuses.Granted or PurchaseStatuses.Paid)
            || p.Provider != PaymentProviders.Razorpay || p.RazorpayPaymentId == null || p.PayableInr <= 0)
            throw PlanServiceException.Conflict(PlanErrorTypes.RefundNotAllowed, "This purchase cannot be refunded.");

        // Renew/Switch share one UserPlan row: only the latest charge on it can be taken back.
        if (p.Status == PurchaseStatuses.Granted && p.GrantedUserPlanId != null
            && await db.PlanPurchases.AsNoTracking().AnyAsync(o => o.Id != p.Id
                && o.GrantedUserPlanId == p.GrantedUserPlanId
                && o.Status == PurchaseStatuses.Granted
                && o.Provider == PaymentProviders.Razorpay && o.PayableInr > 0 && o.RazorpayPaymentId != null
                && (o.GrantedAt > p.GrantedAt || (o.GrantedAt == p.GrantedAt && o.CreatedAt > p.CreatedAt))))
            throw PlanServiceException.Conflict(PlanErrorTypes.RefundNotAllowed, "Refund the latest purchase on this plan first.");

        string refundId;
        try
        {
            refundId = await razorpay.RefundAsync(p.RazorpayPaymentId, await RefundablePaiseAsync(p), $"rf_{p.Id:N}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Refund failed for purchase {PurchaseId}", p.Id);
            throw PlanServiceException.Gateway("Refund could not be started. Please try again.");
        }

        await ApplyRefundAsync(p.Id, refundId, $"Refunded by admin: {reason}");
        logger.LogInformation("Admin {AdminId} refunded purchase {PurchaseId} ({RefundId})", adminId, p.Id, refundId);
        return await ToAdminDtoAsync((await unitOfWork.PlanPurchases.GetByIdAsync(p.Id))!);
    }

    // What Razorpay actually captured (an AmountMismatch row differs from PayableInr by definition).
    private async Task<long> RefundablePaiseAsync(PlanPurchase p)
    {
        if (p.CapturedAmountPaise is > 0) return p.CapturedAmountPaise.Value;
        if (p.RazorpayOrderId != null)
        {
            var attempts = await razorpay.FetchPaymentsForOrderAsync(p.RazorpayOrderId);
            var captured = attempts.FirstOrDefault(a => a.Id == p.RazorpayPaymentId && a.Status == Captured);
            if (captured != null) return captured.AmountPaise;
        }
        return (long)p.PayableInr * 100;
    }

    public async Task<int> CancelActivePlansAsync(Guid userId, string featureKey, string note)
    {
        var now = DateTime.UtcNow;
        var cancelled = await db.UserPlans
            .Where(u => u.UserId == userId && u.FeatureKey == featureKey && u.Status == UserPlanStatuses.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Status, UserPlanStatuses.Cancelled)
                .SetProperty(u => u.EndsAt, u => u.StartsAt < now ? now : u.StartsAt.AddSeconds(1))
                .SetProperty(u => u.Note, note)
                .SetProperty(u => u.UpdatedAt, now));

        var open = await db.PlanPurchases.AsNoTracking()
            .Where(p => p.UserId == userId && p.FeatureKey == featureKey
                && (p.Status == PurchaseStatuses.Created || p.Status == PurchaseStatuses.Pending))
            .Select(p => p.Id)
            .ToListAsync();
        foreach (var id in open)
            await CloseOpenAsync(id, PurchaseStatuses.Expired, "Role changed");
        return cancelled;
    }

    private async Task<AdminPurchaseDto> ToAdminDtoAsync(PlanPurchase p)
    {
        var phone = p.UserId == null ? null : await db.Users.AsNoTracking()
            .Where(u => u.Id == p.UserId)
            .Select(u => u.PhoneNumber)
            .FirstOrDefaultAsync();
        return ToAdminDto(p, phone);
    }

    public static AdminPurchaseDto ToAdminDto(PlanPurchase p, string? phone) => new()
    {
        Id = p.Id,
        UserId = p.UserId,
        UserPhoneMasked = MaskPhone(phone),
        PlanName = p.PlanNameSnapshot,
        FeatureKey = p.FeatureKey,
        Intent = p.Intent,
        CycleMonths = p.CycleMonths,
        GrantedDays = p.GrantedDays,
        ListPriceInr = p.ListPriceInr,
        DiscountInr = p.DiscountInr,
        CreditInr = p.CreditInr,
        PayableInr = p.PayableInr,
        CouponCode = p.CouponCodeSnapshot,
        Status = p.Status,
        Provider = p.Provider,
        RazorpayOrderId = p.RazorpayOrderId,
        RazorpayPaymentId = p.RazorpayPaymentId,
        RazorpayRefundId = p.RazorpayRefundId,
        GrantBlockedReason = p.GrantBlockedReason,
        FailureReason = p.FailureReason,
        CreatedAt = p.CreatedAt,
        GrantedAt = p.GrantedAt,
        RefundedAt = p.RefundedAt,
    };

    public static string? MaskPhone(string? phone)
        => string.IsNullOrEmpty(phone) || phone.Length < 4 ? phone : new string('*', phone.Length - 4) + phone[^4..];
}
