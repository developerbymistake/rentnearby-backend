using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Plan catalog CRUD. Features and prices are replaced as a set; writes are write-through to PlanCatalogCache.
// New plans need no code: every rule reads PlanFeatureKeys through IPlanEntitlementService.
public static class AdminPlanHandlers
{
    public static async Task<IResult> GetPlans(IUnitOfWork unitOfWork, ApplicationDbContext db, string? featureKey = null)
    {
        if (featureKey != null && !PlanFeatures.IsValid(featureKey))
            return BadRequestResponse("Unknown featureKey");
        var plans = await unitOfWork.Plans.GetAllWithDetailsAsync(featureKey);
        var playPrices = await PlayPricesAsync(db);
        return OkResponse(plans.Select(p => ToDto(p, playPrices)).ToList());
    }

    public static async Task<IResult> CreatePlan(
        UpsertPlanRequest request, IValidator<UpsertPlanRequest> validator,
        IUnitOfWork unitOfWork, IPlanCatalogCache catalog, IMemoryCache cache, ApplicationDbContext db)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        if (request.Prices == null || request.Prices.Count == 0)
            return BadRequestResponse("A paid plan needs at least one price.");

        var code = NormalizeCode(request.Code);
        if (await unitOfWork.Plans.CodeExistsAsync(request.FeatureKey, code, null))
            return ConflictResponse("A plan with this code already exists for this feature.", "PlanCodeExists");
        var playProductId = Clean(request.PlayProductId);
        if (playProductId != null && await db.Plans.AnyAsync(p => p.PlayProductId == playProductId))
            return ConflictResponse(PlayIdExistsMessage, PlanErrorTypes.PlayIdExists);

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
            PlayProductId = playProductId,
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
        catch (DbUpdateException ex)
        {
            await catalog.RefreshAsync();
            return IsPlayIdConflict(ex)
                ? ConflictResponse(PlayIdExistsMessage, PlanErrorTypes.PlayIdExists)
                : ConflictResponse("A plan with this code already exists for this feature.", "PlanCodeExists");
        }
        await catalog.RefreshAsync();
        ConfigHandlers.BustPlanDerivedConfig(cache);

        return CreatedResponse(ToDto(plan, await PlayPricesAsync(db)), $"/api/v1/admin/plans/{plan.Id}");
    }

    // The default (free) plan keeps IsDefault, stays enabled and has no prices. Holders of an edited plan
    // get their listing ranks re-applied in one set-based pass, and room/plot listing limits re-applied
    // (over-limit rows plan-paused, newest kept).
    public static async Task<IResult> UpdatePlan(
        Guid id, UpsertPlanRequest request, IValidator<UpsertPlanRequest> validator,
        IUnitOfWork unitOfWork, IPlanCatalogCache catalog, IMemoryCache cache,
        ILiveSlotService slots, IListingCacheInvalidator invalidator, ILoggerFactory loggerFactory, ApplicationDbContext db)
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
            if (!string.IsNullOrWhiteSpace(request.PlayProductId))
                return BadRequestResponse("The default plan cannot have a Play product id.");
        }
        else if (request.Prices == null || request.Prices.Count == 0)
        {
            return BadRequestResponse("A paid plan needs at least one price.");
        }

        var code = NormalizeCode(request.Code);
        if (await unitOfWork.Plans.CodeExistsAsync(plan.FeatureKey, code, plan.Id))
            return ConflictResponse("A plan with this code already exists for this feature.", "PlanCodeExists");
        var playProductId = Clean(request.PlayProductId);
        if (playProductId != null && await db.Plans.AnyAsync(p => p.PlayProductId == playProductId && p.Id != plan.Id))
            return ConflictResponse(PlayIdExistsMessage, PlanErrorTypes.PlayIdExists);

        plan.Code = code;
        plan.Name = request.Name.Trim();
        plan.Tagline = string.IsNullOrWhiteSpace(request.Tagline) ? null : request.Tagline.Trim();
        plan.IsEnabled = request.IsEnabled;
        plan.TrialDays = request.TrialDays;
        plan.SortOrder = request.SortOrder;
        plan.PlayProductId = playProductId;
        plan.UpdatedAt = DateTime.UtcNow;
        unitOfWork.Plans.ReplaceDetails(plan, ToFeatures(request), ToPrices(request));

        catalog.Invalidate();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await unitOfWork.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateException ex)
        {
            await unitOfWork.RollbackTransactionAsync();
            await catalog.RefreshAsync();
            return IsPlayIdConflict(ex)
                ? ConflictResponse(PlayIdExistsMessage, PlanErrorTypes.PlayIdExists)
                : ConflictResponse("A plan with this code already exists for this feature.", "PlanCodeExists");
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
            var fixedRanks = await slots.ReconcileRanksAsync(plan.FeatureKey);
            if (fixedRanks.Rows > 0)
            {
                logger.LogInformation("Plan {PlanId} edit re-ranked {Rows} {Kind} listings", plan.Id, fixedRanks.Rows, plan.FeatureKey);
                await invalidator.BustDistrictsAsync(plan.FeatureKey, fixedRanks.DistrictIds);
            }

            if (PlanFeatures.HasListingSlots(plan.FeatureKey))
            {
                var enforced = await slots.EnforceLimitsAsync(plan.FeatureKey);
                if (enforced.Paused > 0)
                {
                    logger.LogInformation("Plan {PlanId} edit plan-paused {Rows} {Kind} listings of {Owners} owners",
                        plan.Id, enforced.Paused, plan.FeatureKey, enforced.Owners.Count);
                    await invalidator.BustForUsersAsync(enforced.Owners, plan.FeatureKey);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rank/limit re-apply after plan {PlanId} edit failed; the daily reconcile will repair it", plan.Id);
        }

        return OkResponse(ToDto(plan, await PlayPricesAsync(db)));
    }

    private const string PlayIdExistsMessage = "This Google Play product or base plan id is already used by another plan.";

    private sealed class PlayPriceRow
    {
        public string ProductId { get; set; } = string.Empty;
        public string BasePlanId { get; set; } = string.Empty;
        public long RecurringPriceMicros { get; set; }
    }

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsPlayIdConflict(DbUpdateException ex)
        => (ex.InnerException as PostgresException)?.ConstraintName is "ix_plans_play_product" or "ix_planprices_play_baseplan";

    // Google's current INR price per (product, base plan), from the most recently synced subscription.
    private static async Task<Dictionary<(string ProductId, string BasePlanId), int>> PlayPricesAsync(ApplicationDbContext db)
    {
        var rows = await db.Database.SqlQueryRaw<PlayPriceRow>("""
            SELECT DISTINCT ON ("ProductId", "BasePlanId") "ProductId", "BasePlanId", "RecurringPriceMicros"
            FROM "PlaySubscriptions"
            WHERE "BasePlanId" IS NOT NULL AND "RecurringPriceMicros" IS NOT NULL AND "RecurringPriceCurrency" = 'INR'
            ORDER BY "ProductId", "BasePlanId", "LastSyncedAt" DESC NULLS LAST
            """).ToListAsync();
        return rows.ToDictionary(
            r => (r.ProductId, r.BasePlanId),
            r => (int)Math.Round(r.RecurringPriceMicros / 1_000_000m, MidpointRounding.AwayFromZero));
    }

    private static int? PlayPriceOf(Plan p, PlanPrice x, IReadOnlyDictionary<(string ProductId, string BasePlanId), int> playPrices)
        => p.PlayProductId != null && x.PlayBasePlanId != null && playPrices.TryGetValue((p.PlayProductId, x.PlayBasePlanId), out var inr)
            ? inr
            : null;

    private static IEnumerable<PlanFeature> ToFeatures(UpsertPlanRequest request)
        => (request.Features ?? []).Select(f => new PlanFeature { Id = Guid.NewGuid(), Key = f.Key, Value = f.Value });

    private static IEnumerable<PlanPrice> ToPrices(UpsertPlanRequest request)
        => (request.Prices ?? []).Select(p => new PlanPrice
        {
            Id = Guid.NewGuid(),
            CycleMonths = (short)p.CycleMonths,
            PriceInr = p.PriceInr,
            RazorpayPlanId = string.IsNullOrWhiteSpace(p.RazorpayPlanId) ? null : p.RazorpayPlanId.Trim(),
            PlayBasePlanId = Clean(p.PlayBasePlanId),
            PlayTrialOfferId = Clean(p.PlayTrialOfferId),
            IsEnabled = p.IsEnabled,
        });

    internal static AdminPlanDto ToDto(Plan p, IReadOnlyDictionary<(string ProductId, string BasePlanId), int> playPrices) => new()
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
        PlayProductId = p.PlayProductId,
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
                PlayBasePlanId = x.PlayBasePlanId,
                PlayTrialOfferId = x.PlayTrialOfferId,
                PlayRecurringPriceInr = PlayPriceOf(p, x, playPrices),
                PlayPriceMismatch = PlayPriceOf(p, x, playPrices) is { } play && play != x.PriceInr,
            }).ToList(),
        LowPriceWarning = p.Prices.Any(x => x.PriceInr < PlanRules.LowPriceWarningInr),
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };
}
