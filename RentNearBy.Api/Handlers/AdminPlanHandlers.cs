using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Plan catalog CRUD. Features and prices are replaced as a set; writes are write-through to PlanCatalogCache.
// New plans need no code: every rule reads PlanFeatureKeys through IPlanEntitlementService.
public static class AdminPlanHandlers
{
    public static async Task<IResult> GetPlans(IUnitOfWork unitOfWork, string? featureKey = null)
    {
        if (featureKey != null && !PlanFeatures.IsValid(featureKey))
            return BadRequestResponse("Unknown featureKey");
        var plans = await unitOfWork.Plans.GetAllWithDetailsAsync(featureKey);
        return OkResponse(plans.Select(ToDto).ToList());
    }

    public static async Task<IResult> CreatePlan(
        UpsertPlanRequest request, IValidator<UpsertPlanRequest> validator,
        IUnitOfWork unitOfWork, IPlanCatalogCache catalog, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        if (request.Prices == null || request.Prices.Count == 0)
            return BadRequestResponse("A paid plan needs at least one price.");

        var code = NormalizeCode(request.Code);
        if (await unitOfWork.Plans.CodeExistsAsync(request.FeatureKey, code, null))
            return ConflictResponse("A plan with this code already exists for this feature.", "PlanCodeExists");

        var now = DateTime.UtcNow;
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            FeatureKey = request.FeatureKey,
            Code = code,
            Name = request.Name.Trim(),
            Tagline = string.IsNullOrWhiteSpace(request.Tagline) ? null : request.Tagline.Trim(),
            IsDefault = false,
            IsEnabled = request.IsEnabled,
            TrialDays = request.TrialDays,
            SortOrder = request.SortOrder,
            CreatedAt = now,
            UpdatedAt = now,
        };
        unitOfWork.Plans.ReplaceDetails(plan, ToFeatures(request), ToPrices(request));
        await unitOfWork.Plans.AddAsync(plan);

        catalog.Invalidate();
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            await catalog.RefreshAsync();
            return ConflictResponse("A plan with this code already exists for this feature.", "PlanCodeExists");
        }
        await catalog.RefreshAsync();
        ConfigHandlers.BustPlanDerivedConfig(cache);

        return CreatedResponse(ToDto(plan), $"/api/v1/admin/plans/{plan.Id}");
    }

    // The default (free) plan keeps IsDefault, stays enabled and has no prices. Holders of an edited plan
    // get their listing ranks re-applied in one set-based pass.
    public static async Task<IResult> UpdatePlan(
        Guid id, UpsertPlanRequest request, IValidator<UpsertPlanRequest> validator,
        IUnitOfWork unitOfWork, IPlanCatalogCache catalog, IMemoryCache cache,
        ILiveSlotService slots, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(AdminPlanHandlers));
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var plan = await unitOfWork.Plans.GetWithDetailsAsync(id);
        if (plan == null) return NotFoundResponse("Plan not found");
        if (plan.FeatureKey != request.FeatureKey)
            return BadRequestResponse("The feature of a plan cannot be changed.");

        if (plan.IsDefault)
        {
            if (!request.IsEnabled) return BadRequestResponse("The default plan cannot be disabled.");
            if (request.Prices is { Count: > 0 }) return BadRequestResponse("The default plan cannot have prices.");
            if (request.TrialDays > 0) return BadRequestResponse("The default plan cannot have a trial.");
        }
        else if (request.Prices == null || request.Prices.Count == 0)
        {
            return BadRequestResponse("A paid plan needs at least one price.");
        }

        var code = NormalizeCode(request.Code);
        if (await unitOfWork.Plans.CodeExistsAsync(plan.FeatureKey, code, plan.Id))
            return ConflictResponse("A plan with this code already exists for this feature.", "PlanCodeExists");

        plan.Code = code;
        plan.Name = request.Name.Trim();
        plan.Tagline = string.IsNullOrWhiteSpace(request.Tagline) ? null : request.Tagline.Trim();
        plan.IsEnabled = request.IsEnabled;
        plan.TrialDays = request.TrialDays;
        plan.SortOrder = request.SortOrder;
        plan.UpdatedAt = DateTime.UtcNow;
        unitOfWork.Plans.ReplaceDetails(plan, ToFeatures(request), ToPrices(request));

        catalog.Invalidate();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await unitOfWork.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateException)
        {
            await unitOfWork.RollbackTransactionAsync();
            await catalog.RefreshAsync();
            return ConflictResponse("A plan with this code already exists for this feature.", "PlanCodeExists");
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            await catalog.RefreshAsync();
            throw;
        }
        await catalog.RefreshAsync();
        ConfigHandlers.BustPlanDerivedConfig(cache);

        try
        {
            var fixedRows = await slots.ReconcileRanksAsync(plan.FeatureKey);
            if (fixedRows > 0)
                logger.LogInformation("Plan {PlanId} edit re-ranked {Rows} {Kind} listings", plan.Id, fixedRows, plan.FeatureKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rank re-apply after plan {PlanId} edit failed; the daily reconcile will repair it", plan.Id);
        }

        return OkResponse(ToDto(plan));
    }

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    private static IEnumerable<PlanFeature> ToFeatures(UpsertPlanRequest request)
        => (request.Features ?? []).Select(f => new PlanFeature { Id = Guid.NewGuid(), Key = f.Key, Value = f.Value });

    private static IEnumerable<PlanPrice> ToPrices(UpsertPlanRequest request)
        => (request.Prices ?? []).Select(p => new PlanPrice
        {
            Id = Guid.NewGuid(),
            CycleMonths = (short)p.CycleMonths,
            PriceInr = p.PriceInr,
            RazorpayPlanId = string.IsNullOrWhiteSpace(p.RazorpayPlanId) ? null : p.RazorpayPlanId.Trim(),
            IsEnabled = p.IsEnabled,
        });

    internal static AdminPlanDto ToDto(Plan p) => new()
    {
        Id = p.Id,
        FeatureKey = p.FeatureKey,
        Code = p.Code,
        Name = p.Name,
        Tagline = p.Tagline,
        IsDefault = p.IsDefault,
        IsEnabled = p.IsEnabled,
        TrialDays = p.TrialDays,
        SortOrder = p.SortOrder,
        Features = p.Features.OrderBy(f => Array.IndexOf(PlanFeatureKeys.All, f.Key))
            .Select(f => new AdminPlanFeatureDto { Key = f.Key, Value = f.Value }).ToList(),
        Prices = p.Prices.OrderBy(x => x.CycleMonths)
            .Select(x => new AdminPlanPriceDto
            {
                Id = x.Id,
                CycleMonths = x.CycleMonths,
                PriceInr = x.PriceInr,
                RazorpayPlanId = x.RazorpayPlanId,
                IsEnabled = x.IsEnabled,
            }).ToList(),
        LowPriceWarning = p.Prices.Any(x => x.PriceInr < PlanRules.LowPriceWarningInr),
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };
}
