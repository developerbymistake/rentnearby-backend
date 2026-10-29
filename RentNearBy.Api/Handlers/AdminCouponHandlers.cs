using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Plan discount coupons (Percent | Flat | FreePlan). Kind, value and scope lock once redeemed.
public static class AdminCouponHandlers
{
    public static async Task<IResult> GetCoupons(IUnitOfWork unitOfWork, string? status = null, int page = 1, int pageSize = 20)
    {
        if (status != null && status is not (CouponStatuses.Active or CouponStatuses.Revoked or CouponStatuses.Exhausted))
            return BadRequestResponse("Unknown status");
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);
        var (items, total) = await unitOfWork.Coupons.GetPagedAsync(status, page, pageSize);
        return OkResponse(new PagedResult<CouponDto>
        {
            Items = items.Select(ToDto).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
            HasMore = page * pageSize < total,
        });
    }

    public static async Task<IResult> GetCouponById(Guid id, IUnitOfWork unitOfWork)
    {
        var coupon = await unitOfWork.Coupons.GetByIdAsync(id);
        return coupon == null ? NotFoundResponse("Coupon not found") : OkResponse(ToDto(coupon));
    }

    public static async Task<IResult> CreateCoupon(
        CreateCouponRequest request, IValidator<CreateCouponRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, IPlanCatalogCache catalog)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var scopeError = await ValidateScopeAsync(catalog, request.FeatureKey, request.AllowedPlanIds);
        if (scopeError != null) return BadRequestResponse(scopeError);

        var code = request.Code.Trim().ToUpperInvariant();
        if (await unitOfWork.Coupons.CodeExistsAsync(code, null))
            return ConflictResponse("This coupon code already exists.", "CouponCodeExists");

        AdminAuthHandlers.TryGetAdminId(principal, out var adminId);
        var now = DateTime.UtcNow;
        var coupon = new Coupon
        {
            Id = Guid.NewGuid(),
            Code = code,
            Status = CouponStatuses.Active,
            CreatedByAdminId = adminId == Guid.Empty ? null : adminId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(coupon, request.Kind, request.Value, request.MaxDiscountInr, request.MinPlanPriceInr, request.FeatureKey,
            request.AllowedPlanIds, request.NewUsersOnly, request.FirstPurchaseOnly, request.PerUserLimit,
            request.MaxTotalRedemptions, request.ValidFrom, request.ValidUntil, request.AutoApply, request.CampaignLabel);

        await unitOfWork.Coupons.AddAsync(coupon);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ConflictResponse(
                request.AutoApply
                    ? "Another active auto-apply coupon already exists for this feature, or the code is taken."
                    : "This coupon code already exists.",
                "CouponConflict");
        }
        return CreatedResponse(ToDto(coupon), $"/api/v1/admin/coupons/{coupon.Id}");
    }

    public static async Task<IResult> UpdateCoupon(
        Guid id, UpdateCouponRequest request, IValidator<UpdateCouponRequest> validator,
        IUnitOfWork unitOfWork, IPlanCatalogCache catalog)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var coupon = await unitOfWork.Coupons.GetByIdAsync(id);
        if (coupon == null) return NotFoundResponse("Coupon not found");
        if (coupon.Status == CouponStatuses.Revoked)
            return ConflictResponse("A revoked coupon cannot be edited.", PlanErrorTypes.CouponLocked);

        if (coupon.RedemptionCount > 0 && (coupon.Kind != request.Kind || coupon.Value != request.Value
            || coupon.FeatureKey != request.FeatureKey || !SamePlans(coupon.AllowedPlanIds, request.AllowedPlanIds)))
            return ConflictResponse("Kind, value and scope cannot change after the coupon is used.", PlanErrorTypes.CouponLocked);

        var scopeError = await ValidateScopeAsync(catalog, request.FeatureKey, request.AllowedPlanIds);
        if (scopeError != null) return BadRequestResponse(scopeError);

        Apply(coupon, request.Kind, request.Value, request.MaxDiscountInr, request.MinPlanPriceInr, request.FeatureKey,
            request.AllowedPlanIds, request.NewUsersOnly, request.FirstPurchaseOnly, request.PerUserLimit,
            request.MaxTotalRedemptions, request.ValidFrom, request.ValidUntil, request.AutoApply, request.CampaignLabel);
        coupon.Status = coupon.MaxTotalRedemptions is { } max && coupon.RedemptionCount >= max
            ? CouponStatuses.Exhausted
            : CouponStatuses.Active;
        coupon.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.Coupons.UpdateAsync(coupon);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ConflictResponse("Another active auto-apply coupon already exists for this feature.", "CouponConflict");
        }
        return OkResponse(ToDto(coupon));
    }

    public static async Task<IResult> RevokeCoupon(Guid id, IUnitOfWork unitOfWork)
    {
        var coupon = await unitOfWork.Coupons.GetByIdAsync(id);
        if (coupon == null) return NotFoundResponse("Coupon not found");
        await unitOfWork.Coupons.RevokeAsync(id);
        return OkResponse(new { success = true, status = CouponStatuses.Revoked });
    }

    public static async Task<IResult> GetRedemptions(Guid id, IUnitOfWork unitOfWork, int page = 1, int pageSize = 20)
    {
        if (await unitOfWork.Coupons.GetByIdAsync(id) == null) return NotFoundResponse("Coupon not found");
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);
        var (items, hasMore) = await unitOfWork.CouponRedemptions.GetByCouponPagedAsync(id, page, pageSize);
        return OkResponse(new
        {
            items = items.Select(r => new CouponRedemptionDto
            {
                Id = r.Id,
                UserId = r.UserId,
                UserPhoneMasked = PlanPurchaseService.MaskPhone(r.UserPhone),
                PurchaseId = r.PurchaseId,
                PlanName = r.PlanName,
                DiscountInr = r.DiscountInr,
                Status = r.Status,
                CreatedAt = r.CreatedAt,
            }).ToList(),
            hasMore,
        });
    }

    // Allowed plans must exist, be paid plans and match the coupon's feature (when set).
    private static async Task<string?> ValidateScopeAsync(IPlanCatalogCache catalog, string? featureKey, List<Guid>? planIds)
    {
        if (planIds == null || planIds.Count == 0) return null;
        var snapshot = await catalog.GetAsync();
        foreach (var planId in planIds)
        {
            var plan = snapshot.GetPlan(planId);
            if (plan == null || plan.IsDefault) return "Allowed plans must be existing paid plans.";
            if (featureKey != null && plan.FeatureKey != featureKey) return "Allowed plans must belong to the coupon's feature.";
        }
        return null;
    }

    private static bool SamePlans(Guid[]? current, List<Guid>? next)
    {
        var a = current ?? [];
        var b = next ?? [];
        return a.Length == b.Count && a.OrderBy(x => x).SequenceEqual(b.OrderBy(x => x));
    }

    private static void Apply(
        Coupon c, string kind, int value, int? maxDiscountInr, int? minPlanPriceInr, string? featureKey,
        List<Guid>? allowedPlanIds, bool newUsersOnly, bool firstPurchaseOnly, int perUserLimit,
        int? maxTotalRedemptions, DateTime? validFrom, DateTime? validUntil, bool autoApply, string? campaignLabel)
    {
        c.Kind = kind;
        c.Value = kind == CouponKinds.FreePlan ? 0 : value;
        c.MaxDiscountInr = kind == CouponKinds.Percent ? maxDiscountInr : null;
        c.MinPlanPriceInr = minPlanPriceInr;
        c.FeatureKey = featureKey;
        c.AllowedPlanIds = allowedPlanIds is { Count: > 0 } ? allowedPlanIds.Distinct().ToArray() : null;
        c.NewUsersOnly = newUsersOnly;
        c.FirstPurchaseOnly = firstPurchaseOnly;
        c.PerUserLimit = perUserLimit;
        c.MaxTotalRedemptions = maxTotalRedemptions;
        c.ValidFrom = validFrom?.ToUniversalTime();
        c.ValidUntil = validUntil?.ToUniversalTime();
        c.AutoApply = autoApply;
        c.CampaignLabel = string.IsNullOrWhiteSpace(campaignLabel) ? null : campaignLabel.Trim();
    }

    private static CouponDto ToDto(Coupon c) => new()
    {
        Id = c.Id,
        Code = c.Code,
        Kind = c.Kind,
        Value = c.Value,
        MaxDiscountInr = c.MaxDiscountInr,
        MinPlanPriceInr = c.MinPlanPriceInr,
        FeatureKey = c.FeatureKey,
        AllowedPlanIds = c.AllowedPlanIds?.ToList(),
        NewUsersOnly = c.NewUsersOnly,
        FirstPurchaseOnly = c.FirstPurchaseOnly,
        PerUserLimit = c.PerUserLimit,
        MaxTotalRedemptions = c.MaxTotalRedemptions,
        RedemptionCount = c.RedemptionCount,
        ValidFrom = c.ValidFrom,
        ValidUntil = c.ValidUntil,
        AutoApply = c.AutoApply,
        Status = c.Status,
        CampaignLabel = c.CampaignLabel,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };
}
