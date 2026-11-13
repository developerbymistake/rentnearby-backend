using FluentValidation;
using Mapster;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.Features;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Extensions;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RentNearBy.Api.Handlers;

public static class AdminHandlers
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public static async Task<IResult> GetDistricts(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue("districts", out List<DistrictDto>? cached) || cached == null)
        {
            cached = await unitOfWork.Districts.GetAllDtosAsync();
            cache.Set("districts", cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    // Simplification tolerance in degrees for the boundary the client renders as a map overlay — the
    // full-fidelity District.Boundary column stays untouched and remains the authoritative ST_Contains gate
    // at /locations/context.
    private const double BoundarySimplifyToleranceDegrees = 0.0005;

    public static async Task<IResult> GetDistrictBoundary(Guid id, ApplicationDbContext db, IMemoryCache cache)
    {
        var cacheKey = $"district_boundary_{id}";
        if (!cache.TryGetValue(cacheKey, out FeatureCollection? cached) || cached == null)
        {
            var row = await db.Districts
                .Where(d => d.Id == id && d.IsActive)
                .Select(d => new { d.Id, d.Name, d.Boundary })
                .FirstOrDefaultAsync();

            if (row == null || row.Boundary == null)
                return NotFoundResponse("District boundary not found");

            var simplified = NetTopologySuite.Simplify.TopologyPreservingSimplifier.Simplify(
                row.Boundary, BoundarySimplifyToleranceDegrees);

            var attributes = new AttributesTable();
            attributes.Add("districtId", row.Id);
            attributes.Add("name", row.Name);
            var feature = new Feature(simplified, attributes);
            cached = new FeatureCollection { feature };
            cache.Set(cacheKey, cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public record ToggleDistrictRequest(bool IsActive);

    private static async Task FlushContextCacheAsync(IConnectionMultiplexer? redis)
    {
        if (redis == null) return;
        try
        {
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
            if (server == null) return;
            var redisDb = redis.GetDatabase();
            foreach (var pattern in ListingCacheKeys.ContextPatterns)
                await foreach (var key in server.KeysAsync(pattern: pattern))
                    await redisDb.KeyDeleteAsync(key);
        }
        catch { /* best-effort: TTL (10 min) covers Redis failures */ }
    }

    public static async Task<IResult> ToggleDistrictActive(
        Guid id, ToggleDistrictRequest request,
        ApplicationDbContext db, IMemoryCache cache, IServiceProvider sp)
    {
        var district = await db.Districts.FindAsync(id);
        if (district == null) return NotFoundResponse("District not found");

        district.IsActive = request.IsActive;
        await db.SaveChangesAsync();
        cache.Remove("states");
        cache.Remove("districts");
        cache.Remove($"cities_{id}");
        cache.Remove("cities_all");
        cache.Remove($"district_boundary_{id}");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());

        return OkResponse(new { success = true, isActive = district.IsActive });
    }

    public record ToggleOnboardingRequest(bool Enabled);

    public static async Task<IResult> ToggleDistrictOnboarding(
        Guid id, ToggleOnboardingRequest request,
        ApplicationDbContext db, IMemoryCache cache, IServiceProvider sp)
    {
        var district = await db.Districts.FindAsync(id);
        if (district == null) return NotFoundResponse("District not found");

        district.BusinessOnboardingEnabled = request.Enabled;
        await db.SaveChangesAsync();
        cache.Remove("states");
        cache.Remove("districts");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());

        return OkResponse(new { success = true, businessOnboardingEnabled = district.BusinessOnboardingEnabled });
    }

    public static async Task<IResult> CreateDistrict(
        CreateDistrictRequest request,
        IValidator<CreateDistrictRequest> validator,
        ApplicationDbContext db,
        IGeocodingService geocoding,
        IMemoryCache cache,
        IServiceProvider sp)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var trimmedName = request.Name.Trim();
        var trimmedState = request.StateName.Trim();

        var exists = await db.Districts.AnyAsync(d =>
            d.Name.ToLower() == trimmedName.ToLower() &&
            d.StateName.ToLower() == trimmedState.ToLower());
        if (exists)
            return BadRequestResponse($"District '{trimmedName}' already exists in {trimmedState}.", "DuplicateDistrict");

        var boundary = await geocoding.FetchDistrictBoundaryAsync(trimmedName, trimmedState);

        var district = new District
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            StateName = trimmedState,
            IsActive = false,
            Boundary = boundary,
            CreatedAt = DateTime.UtcNow,
        };

        db.Districts.Add(district);
        await db.SaveChangesAsync();

        cache.Remove("states");
        cache.Remove("districts");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());

        return CreatedResponse(district.Adapt<DistrictDto>(), $"/api/v1/admin/districts/{district.Id}");
    }

    public static async Task<IResult> GetStates(ApplicationDbContext db, IMemoryCache cache)
    {
        if (!cache.TryGetValue("states", out List<StateDto>? cached) || cached == null)
        {
            cached = await db.Districts
                .GroupBy(d => d.StateName)
                .Select(g => new StateDto
                {
                    Name = g.Key,
                    TotalDistricts = g.Count(),
                    ActiveDistricts = g.Count(d => d.IsActive),
                    OnboardingDistricts = g.Count(d => d.BusinessOnboardingEnabled),
                })
                .OrderBy(s => s.Name)
                .ToListAsync();
            cache.Set("states", cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public static async Task<IResult> BulkToggleStateActive(
        string stateName, ToggleDistrictRequest request,
        ApplicationDbContext db, IMemoryCache cache, IServiceProvider sp)
    {
        var affectedIds = await db.Districts
            .Where(d => d.StateName == stateName)
            .Select(d => d.Id)
            .ToListAsync();

        var updated = await db.Districts
            .Where(d => d.StateName == stateName)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsActive, request.IsActive));

        if (updated == 0)
            return NotFoundResponse($"No districts found for state '{stateName}'");

        cache.Remove("states");
        cache.Remove("districts");
        cache.Remove("cities_all");
        foreach (var districtId in affectedIds)
            cache.Remove($"district_boundary_{districtId}");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());

        return OkResponse(new { success = true, isActive = request.IsActive, updatedCount = updated });
    }

    public static async Task<IResult> BulkToggleStateOnboarding(
        string stateName, ToggleOnboardingRequest request,
        ApplicationDbContext db, IMemoryCache cache, IServiceProvider sp)
    {
        var updated = await db.Districts
            .Where(d => d.StateName == stateName)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.BusinessOnboardingEnabled, request.Enabled));

        if (updated == 0)
            return NotFoundResponse($"No districts found for state '{stateName}'");

        cache.Remove("states");
        cache.Remove("districts");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());

        return OkResponse(new { success = true, businessOnboardingEnabled = request.Enabled, updatedCount = updated });
    }

    public static async Task<IResult> ForceActivateDistrict(
        Guid id, ApplicationDbContext db, IMemoryCache cache, IServiceProvider sp)
    {
        var district = await db.Districts.FindAsync(id);
        if (district == null) return NotFoundResponse("District not found");
        district.IsActive = true;
        await db.SaveChangesAsync();
        cache.Remove("states");
        cache.Remove("districts");
        cache.Remove($"cities_{id}");
        cache.Remove("cities_all");
        cache.Remove($"district_boundary_{id}");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());
        return OkResponse(new { success = true, isActive = true });
    }

    public static async Task<IResult> DeleteDistrict(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db, IServiceProvider sp)
    {
        var district = await unitOfWork.Districts.GetByIdAsync(id);
        if (district == null) return NotFoundResponse("District not found");

        // StayListing.DistrictId is FK Restrict, so any stay row (deleted ones too) blocks the delete.
        if (await db.StayListings.AnyAsync(s => s.DistrictId == id))
            return BadRequestResponse("Cannot delete district with stays");

        // TaxiListing.DistrictId is FK Restrict too.
        if (await db.TaxiListings.AnyAsync(t => t.DistrictId == id))
            return BadRequestResponse("Cannot delete district with taxis");

        // Trip.DistrictId is FK Restrict too.
        if (await db.Trips.AnyAsync(t => t.DistrictId == id))
            return BadRequestResponse("Cannot delete district with trips");

        await unitOfWork.Districts.DeleteAsync(district);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("states");
        cache.Remove("districts");
        cache.Remove($"cities_{id}");
        cache.Remove("cities_all");
        cache.Remove($"district_boundary_{id}");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());

        return NoContentResponse();
    }

    public static async Task<IResult> GetCities(Guid? districtId, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var cacheKey = districtId.HasValue ? $"cities_{districtId}" : "cities_all";
        if (!cache.TryGetValue(cacheKey, out List<CityDto>? cached) || cached == null)
        {
            cached = districtId.HasValue
                ? await unitOfWork.Cities.GetDtosByDistrictIdAsync(districtId.Value)
                : await unitOfWork.Cities.GetAllDtosAsync();
            cache.Set(cacheKey, cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public static async Task<IResult> CreateCity(CreateCityRequest request, IValidator<CreateCityRequest> validator, IUnitOfWork unitOfWork, IGeocodingService geocoding, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var trimmedName = request.Name.Trim();
        var exists = await unitOfWork.Cities.GetByDistrictIdAsync(request.DistrictId);
        if (exists.Any(c => string.Equals(c.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
            return BadRequestResponse($"City '{trimmedName}' already exists in this district", "DuplicateCity");

        decimal lat, lng;
        if (request.Latitude.HasValue && request.Longitude.HasValue)
        {
            lat = request.Latitude.Value;
            lng = request.Longitude.Value;
        }
        else
        {
            var district = await unitOfWork.Districts.GetByIdAsync(request.DistrictId);
            if (district is null) return NotFoundResponse("District not found");

            var point = await geocoding.GeocodeAsync($"{trimmedName}, {district.Name}, India");
            if (point is null)
                return BadRequestResponse($"Could not geocode '{trimmedName}'. Provide coordinates manually.");
            lat = point.Latitude;
            lng = point.Longitude;
        }

        var city = new City { Id = Guid.NewGuid(), DistrictId = request.DistrictId, Name = trimmedName, Latitude = lat, Longitude = lng, CreatedAt = DateTime.UtcNow };
        await unitOfWork.Cities.AddAsync(city);
        await unitOfWork.SaveChangesAsync();

        cache.Remove($"cities_{request.DistrictId}");
        cache.Remove("cities_all");

        return CreatedResponse(city.Adapt<CityDto>(), $"/api/v1/admin/cities/{city.Id}");
    }

    public static async Task<IResult> Geocode(string q, IGeocodingService geocoding)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequestResponse("Query parameter 'q' is required");

        var point = await geocoding.GeocodeAsync(q.Trim());
        if (point is null)
            return NotFoundResponse("No results found for the given query");

        return OkResponse(new { latitude = point.Latitude, longitude = point.Longitude, displayName = point.DisplayName });
    }

    public static async Task<IResult> DeleteCity(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var city = await unitOfWork.Cities.GetByIdAsync(id);
        if (city == null) return NotFoundResponse("City not found");

        await unitOfWork.Cities.DeleteAsync(city);
        await unitOfWork.SaveChangesAsync();

        cache.Remove($"cities_{city.DistrictId}");
        cache.Remove("cities_all");

        return NoContentResponse();
    }

    public static async Task<IResult> GetReportReasons(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue("report_reasons", out List<ReportReasonDto>? cached) || cached == null)
        {
            var reasons = await unitOfWork.ReportReasons.GetAllAsync();
            cached = reasons.OrderBy(r => r.SortOrder).Select(r => r.Adapt<ReportReasonDto>()).ToList();
            cache.Set("report_reasons", cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public static async Task<IResult> CreateReportReason(CreateReportReasonRequest request, IValidator<CreateReportReasonRequest> validator, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var reason = new ReportReason { Id = Guid.NewGuid(), Name = request.Name.Trim(), Description = request.Description, SortOrder = request.SortOrder, CreatedAt = DateTime.UtcNow };
        await unitOfWork.ReportReasons.AddAsync(reason);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("report_reasons");

        return CreatedResponse(reason.Adapt<ReportReasonDto>(), $"/api/v1/admin/report-reasons/{reason.Id}");
    }

    public static async Task<IResult> UpdateReportReason(Guid id, UpdateReportReasonRequest request, IValidator<UpdateReportReasonRequest> validator, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var reason = await unitOfWork.ReportReasons.GetByIdAsync(id);
        if (reason == null) return NotFoundResponse("Report reason not found");

        if (request.Name != null) reason.Name = request.Name.Trim();
        if (request.Description != null) reason.Description = request.Description;

        await unitOfWork.ReportReasons.UpdateAsync(reason);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("report_reasons");

        return OkResponse(reason.Adapt<ReportReasonDto>());
    }

    public static async Task<IResult> DeleteReportReason(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var reason = await unitOfWork.ReportReasons.GetByIdAsync(id);
        if (reason == null) return NotFoundResponse("Report reason not found");

        if (DataSeeder.IsSeededReportReason(id))
            return BadRequestResponse("Built-in reason. Rename it instead");

        if (await db.ListingReports.AnyAsync(r => r.ReasonId == id))
            return BadRequestResponse("Cannot delete a reason that is referenced by existing reports");

        await unitOfWork.ReportReasons.DeleteAsync(reason);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("report_reasons");

        return NoContentResponse();
    }

    public static async Task<IResult> GetStats(ApplicationDbContext db, IPlanCatalogCache catalog)
    {
        var totalUsers = await db.Users.CountAsync();
        var activeDistricts = await db.Districts.CountAsync(d => d.IsActive);

        // Revenue = granted paid plan purchases, Razorpay and Play (refunded ones drop out), summed in paise.
        var now = DateTime.UtcNow;
        var monthStart = PlanTime.IstMonthStartUtc(now);
        var revenue = db.PlanPurchases.Where(p => p.Status == PurchaseStatuses.Granted && p.Provider != PaymentProviders.None);
        var totalEarningsPaise = await revenue.SumAsync(p => (long?)(p.CapturedAmountPaise ?? (long)p.PayableInr * 100)) ?? 0;
        var currentMonthEarningsPaise = await revenue.Where(p => p.GrantedAt >= monthStart)
            .SumAsync(p => (long?)(p.CapturedAmountPaise ?? (long)p.PayableInr * 100)) ?? 0;

        var planRows = await db.UserPlans.AsNoTracking()
            .Where(u => u.Status == UserPlanStatuses.Active && u.EndsAt > now && !u.Plan.IsDefault)
            .GroupBy(u => new { u.FeatureKey, u.PlanId, PlanName = u.Plan.Name })
            .Select(g => new
            {
                g.Key.FeatureKey,
                g.Key.PlanId,
                g.Key.PlanName,
                Paid = g.Count(u => (u.Source == UserPlanSources.Subscription || u.Source == UserPlanSources.Prepaid)
                    && (u.PlayTrialEndsAt == null || u.PlayTrialEndsAt <= now)),
                Trial = g.Count(u => u.Source == UserPlanSources.Trial || (u.PlayTrialEndsAt != null && u.PlayTrialEndsAt > now)),
                Granted = g.Count(u => u.Source == UserPlanSources.AdminGrant),
            })
            .ToListAsync();

        var snapshot = await catalog.GetAsync();
        var countsByPlan = planRows.ToDictionary(r => (r.FeatureKey, r.PlanId));
        var planUsers = new List<AdminPlanUsersDto>();
        foreach (var feature in PlanFeatures.All)
        {
            foreach (var plan in snapshot.PlansOf(feature).Where(p => !p.IsDefault))
            {
                countsByPlan.TryGetValue((feature, plan.Id), out var row);
                if (!plan.IsEnabled && row == null) continue;
                planUsers.Add(new AdminPlanUsersDto
                {
                    FeatureKey = feature,
                    PlanId = plan.Id,
                    PlanName = plan.Name,
                    PaidUsers = row?.Paid ?? 0,
                    TrialUsers = row?.Trial ?? 0,
                    GrantedUsers = row?.Granted ?? 0,
                });
                countsByPlan.Remove((feature, plan.Id));
            }
        }
        planUsers.AddRange(planRows
            .Where(r => countsByPlan.ContainsKey((r.FeatureKey, r.PlanId)))
            .OrderBy(r => r.FeatureKey).ThenBy(r => r.PlanName)
            .Select(r => new AdminPlanUsersDto
            {
                FeatureKey = r.FeatureKey,
                PlanId = r.PlanId,
                PlanName = r.PlanName,
                PaidUsers = r.Paid,
                TrialUsers = r.Trial,
                GrantedUsers = r.Granted,
            }));

        var istDayStart = TripExecutionRules.IstMidnightUtc(TripExecutionRules.TodayIst(now));
        var tripEnquiriesToday = await db.TripEnquiries.CountAsync(e => e.CreatedAt >= istDayStart);
        // Live open = Open status and not yet checked out; an ended row waits in its own admin bucket.
        var todayIst = TripExecutionRules.TodayIst(now);
        var tripEnquiriesOpen = await db.TripEnquiries.CountAsync(e =>
            TripEnquiryStatuses.Open.Contains(e.Status) && (e.CheckOut == null || e.CheckOut >= todayIst));
        var weekAgo = now.AddDays(-7);
        var tripProblems7d = await db.TripProblemLogs.CountAsync(p => p.CreatedAt >= weekAgo);
        var tripConfirmedThisWeek = await db.TripEnquiries.CountAsync(e =>
            e.Status == TripEnquiryStatuses.ConfirmedByBoth && e.ConfirmedByOwnerAt >= weekAgo);
        var leadsReceived = await db.TaxiListings.SumAsync(t => (long?)t.TripLeadsReceived) ?? 0;
        var leadsAnswered = await db.TaxiListings.SumAsync(t => (long?)t.TripLeadsAnsweredInApp) ?? 0;
        var tripLeadsAnsweredPct = leadsReceived == 0 ? 0 : (int)Math.Min(100, leadsAnswered * 100 / leadsReceived);

        return OkResponse(new AdminStatsDto
        {
            TotalUsers = totalUsers,
            ActiveDistricts = activeDistricts,
            TotalEarnings = Money.RupeesFromPaise(totalEarningsPaise),
            CurrentMonthEarnings = Money.RupeesFromPaise(currentMonthEarningsPaise),
            TotalEarningsPaise = totalEarningsPaise,
            CurrentMonthEarningsPaise = currentMonthEarningsPaise,
            ActivePaidUsers = planRows.Sum(r => r.Paid),
            PlanUsers = planUsers,
            TripEnquiriesToday = tripEnquiriesToday,
            TripEnquiriesOpen = tripEnquiriesOpen,
            TripProblems7d = tripProblems7d,
            TripConfirmedThisWeek = tripConfirmedThisWeek,
            TripLeadsAnsweredPct = tripLeadsAnsweredPct,
        });
    }

    // ── Payment Kill Switch ─────────────────────────────────────────────────────
    // Single global row (RentNearBy.Core.Models.AppFeatureKeys.PaymentEnabled) in the generic
    // AppFeatureFlag table; also holds the free-mode listing limit.

    public static async Task<IResult> GetPaymentFeature(IUnitOfWork unitOfWork)
    {
        var flag = await unitOfWork.AppFeatureFlags.GetByKeyAsync(AppFeatureKeys.PaymentEnabled);
        if (flag == null) return NotFoundResponse("Payment feature flag not found");

        return OkResponse(ToPaymentFeatureDto(flag));
    }

    private static object ToPaymentFeatureDto(AppFeatureFlag flag) => new
    {
        isEnabled = flag.IsEnabled,
        freeDurationDays = flag.FreeDurationDays ?? 30,
        freeModeListingLimit = flag.FreeModeListingLimit,
        provider = flag.PaymentProvider,
        updatedByAdminId = flag.UpdatedByAdminId,
        updatedAt = flag.UpdatedAt,
        reason = flag.Reason,
    };

    public static async Task<IResult> UpdatePaymentFeature(
        UpdatePaymentFeatureRequest request, IValidator<UpdatePaymentFeatureRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPaymentFeatureReader paymentReader)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var flag = await unitOfWork.AppFeatureFlags.GetByKeyAsync(AppFeatureKeys.PaymentEnabled);
        if (flag == null) return NotFoundResponse("Payment feature flag not found");

        if (request.IsEnabled.HasValue) flag.IsEnabled = request.IsEnabled.Value;
        if (request.FreeDurationDays.HasValue) flag.FreeDurationDays = request.FreeDurationDays.Value;
        if (request.ClearFreeModeListingLimit) flag.FreeModeListingLimit = null;
        else if (request.FreeModeListingLimit.HasValue) flag.FreeModeListingLimit = request.FreeModeListingLimit.Value;
        if (request.Reason != null) flag.Reason = request.Reason;
        if (request.Provider != null) flag.PaymentProvider = request.Provider;

        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        flag.UpdatedByAdminId = Guid.TryParse(adminIdClaim, out var parsedAdminId) ? parsedAdminId : null;
        flag.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.AppFeatureFlags.UpdateAsync(flag);
        await unitOfWork.SaveChangesAsync();

        paymentReader.Invalidate();

        return OkResponse(ToPaymentFeatureDto(flag));
    }

    // ── App Tabs (master table for the consumer app's bottom-nav) ───────────────
    // Seeded rows (RentNearBy.Core.Models.AppTabKeys) — renaming applies to any row; deactivating applies to the
    // five business tabs (Home/Profile are structural, rejected below). An off tab's consumer route group is
    // rejected by Filters/TabGateFilter.cs, its Home sections come back null and its linked role
    // (AppTabKeys.LinkedRoleKey) is hidden from new choices; existing holders keep the role in the DB.

    public static async Task<IResult> GetAppTabs(IUnitOfWork unitOfWork)
    {
        var tabs = await unitOfWork.AppTabs.GetAllAsync();
        return OkResponse(tabs.OrderBy(t => t.SortOrder).Select(ToAdminAppTabDto));
    }

    public static async Task<IResult> UpdateAppTab(
        string tabKey, UpdateAppTabRequest request, IValidator<UpdateAppTabRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var tab = await unitOfWork.AppTabs.GetByKeyAsync(tabKey);
        if (tab == null) return NotFoundResponse($"No app tab for key '{tabKey}'");

        if (request.IsActive == false && AppTabKeys.NonDeactivatable.Contains(tabKey))
            return BadRequestResponse($"'{tab.DisplayName}' is a structural tab and cannot be deactivated");

        if (request.DisplayName != null) tab.DisplayName = request.DisplayName;
        if (request.IsActive.HasValue) tab.IsActive = request.IsActive.Value;
        if (request.Reason != null) tab.Reason = request.Reason;

        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        tab.UpdatedByAdminId = Guid.TryParse(adminIdClaim, out var parsedAdminId) ? parsedAdminId : null;
        tab.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.AppTabs.UpdateAsync(tab);

        // Write-through: invalidate before the commit and again after it, then re-read and set. Each invalidate
        // bumps the cache version, so any reader that started before the commit skips its cache write.
        AppTabCache.Invalidate(cache);
        await unitOfWork.SaveChangesAsync();
        AppTabCache.Invalidate(cache);
        await AppTabCache.RefreshAsync(unitOfWork, cache);

        return OkResponse(ToAdminAppTabDto(tab));
    }

    private static AdminAppTabDto ToAdminAppTabDto(AppTab t) => new()
    {
        Id = t.Id, TabKey = t.TabKey, DisplayName = t.DisplayName, IsActive = t.IsActive, SortOrder = t.SortOrder,
        CanDeactivate = !AppTabKeys.NonDeactivatable.Contains(t.TabKey),
        ApiGated = AppTabKeys.ApiGated.Contains(t.TabKey),
        LinkedRoleKey = AppTabKeys.LinkedRoleKey(t.TabKey),
        UpdatedByAdminId = t.UpdatedByAdminId, UpdatedAt = t.UpdatedAt, Reason = t.Reason,
    };

    // ── App Version / Updates ───────────────────────────────────────────────────
    // Reason describes this change only, so it is overwritten on every PUT (unlike payment-feature).

    public static async Task<IResult> GetAppVersionConfigs(IUnitOfWork unitOfWork)
    {
        var rows = await unitOfWork.AppVersionConfigs.GetAllWithAdminAsync();
        return OkResponse(rows
            .OrderBy(r => Array.IndexOf(AppPlatforms.All, r.Platform))
            .Select(r => ToAdminAppVersionDto(r, r.UpdatedByAdmin))
            .ToList());
    }

    public static async Task<IResult> UpdateAppVersionConfig(
        string platform, UpdateAppVersionConfigRequest request, IValidator<UpdateAppVersionConfigRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!AppPlatforms.TryNormalize(platform, out var key))
            return BadRequestResponse("platform must be 'android' or 'ios'");

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var row = await unitOfWork.AppVersionConfigs.GetByPlatformAsync(key);
        if (row == null) return NotFoundResponse($"No app version config for platform '{key}'");

        var wasEnabled = row.IsEnabled;
        var previousMin = row.MinBuildNumber;
        var previousLatest = row.LatestBuildNumber;

        if (request.IsEnabled.HasValue) row.IsEnabled = request.IsEnabled.Value;
        if (request.MinBuildNumber.HasValue) row.MinBuildNumber = request.MinBuildNumber.Value;
        if (request.MinVersionName != null) row.MinVersionName = request.MinVersionName;
        if (request.LatestBuildNumber.HasValue) row.LatestBuildNumber = request.LatestBuildNumber.Value;
        if (request.LatestVersionName != null) row.LatestVersionName = request.LatestVersionName;
        if (request.StoreUrl != null) row.StoreUrl = string.IsNullOrWhiteSpace(request.StoreUrl) ? null : request.StoreUrl.Trim();
        if (request.Message != null) row.Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
        if (request.ForceCacheMaxAgeHours.HasValue) row.ForceCacheMaxAgeHours = request.ForceCacheMaxAgeHours.Value;
        row.Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();

        if (row.MinBuildNumber > row.LatestBuildNumber)
            return BadRequestResponse("Minimum build cannot be greater than latest build", "VERSION_RANGE_INVALID");

        if (row.StoreUrl != null)
        {
            var expectedHost = AppPlatforms.StoreHosts[key];
            if (!Uri.TryCreate(row.StoreUrl, UriKind.Absolute, out var storeUri)
                || storeUri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(storeUri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
                return BadRequestResponse($"Store URL for {key} must be an https link on {expectedHost}");

            if (key == AppPlatforms.Android
                && (!string.Equals(storeUri.AbsolutePath.TrimEnd('/'), AppPlatforms.AndroidStoreDetailsPath, StringComparison.OrdinalIgnoreCase)
                    || !QueryHelpers.ParseQuery(storeUri.Query).TryGetValue("id", out var packageIds)
                    || packageIds.Count != 1
                    || !string.Equals(packageIds[0], AppPlatforms.AndroidPackageId, StringComparison.Ordinal)))
                return BadRequestResponse($"Store URL for {key} must be {AppPlatforms.AndroidStoreUrl}");
        }

        if (row.IsEnabled && row.StoreUrl == null)
            return BadRequestResponse("Store URL is required while update checks are enabled", "STORE_URL_REQUIRED");

        if (row.IsEnabled && request.ConfirmUnseenBuild != true)
        {
            var ceiling = Math.Max(row.MaxSeenBuild, AppVersionRules.FirstGatedBuild);
            var enabling = !wasEnabled;

            if ((enabling || row.MinBuildNumber != previousMin) && row.MinBuildNumber > ceiling)
                return BadRequestResponse(
                    $"Minimum build {row.MinBuildNumber} has not been reported by any installed app yet (highest seen: {row.MaxSeenBuild}). Install it from the store first.",
                    "BUILD_NOT_SEEN");

            if ((enabling || row.LatestBuildNumber != previousLatest) && row.LatestBuildNumber > ceiling)
                return BadRequestResponse(
                    $"Latest build {row.LatestBuildNumber} has not been reported by any installed app yet (highest seen: {row.MaxSeenBuild}). Install it from the store first.",
                    "BUILD_NOT_SEEN");
        }

        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        row.UpdatedByAdminId = Guid.TryParse(adminIdClaim, out var parsedAdminId) ? parsedAdminId : null;
        row.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.SaveChangesAsync();

        cache.Remove(ConfigHandlers.AppVersionCacheKey(key));

        var admin = row.UpdatedByAdminId.HasValue ? await unitOfWork.Admins.GetByIdAsync(row.UpdatedByAdminId.Value) : null;
        return OkResponse(ToAdminAppVersionDto(row, admin));
    }

    private static AdminAppVersionConfigDto ToAdminAppVersionDto(AppVersionConfig row, Admin? updatedBy) => new()
    {
        Id = row.Id,
        Platform = row.Platform,
        IsEnabled = row.IsEnabled,
        MinBuildNumber = row.MinBuildNumber,
        MinVersionName = row.MinVersionName,
        LatestBuildNumber = row.LatestBuildNumber,
        LatestVersionName = row.LatestVersionName,
        StoreUrl = row.StoreUrl,
        Message = row.Message,
        ForceCacheMaxAgeHours = row.ForceCacheMaxAgeHours,
        MaxSeenBuild = row.MaxSeenBuild,
        FirstGatedBuild = AppVersionRules.FirstGatedBuild,
        UpdatedByAdminId = row.UpdatedByAdminId,
        UpdatedByAdminName = updatedBy == null ? null : (string.IsNullOrWhiteSpace(updatedBy.Name) ? updatedBy.Email : updatedBy.Name),
        UpdatedAt = row.UpdatedAt,
        Reason = row.Reason,
    };

    public static async Task<IResult> GetUsers(
        ApplicationDbContext db,
        IRoleChangeGuardService guard,
        int page = 1,
        int pageSize = 20,
        bool? isActive = null,
        string? search = null,
        Guid? districtId = null,
        Guid? cityId = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.Users.AsNoTracking().AsQueryable();

        if (isActive.HasValue)
            query = query.Where(u => u.IsActive == isActive.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u =>
                u.PhoneNumber.Contains(s) ||
                (u.Name != null && u.Name.ToLower().Contains(s)));
        }

        if (districtId.HasValue)
        {
            var d = districtId.Value;
            query = query.Where(u =>
                db.StayListings.Any(s => s.UserId == u.Id && s.DistrictId == d && !s.IsDeleted)
                || db.TaxiListings.Any(t => t.UserId == u.Id && t.DistrictId == d && !t.IsDeleted));
        }

        if (cityId.HasValue)
        {
            var c = cityId.Value;
            query = query.Where(u =>
                db.StayListings.Any(s => s.UserId == u.Id && s.CityId == c && !s.IsDeleted)
                || db.TaxiListings.Any(t => t.UserId == u.Id && t.CityId == c && !t.IsDeleted));
        }

        var now = DateTime.UtcNow;
        var projected = query.Select(u => new
        {
            User = u,
            RoleKey = u.Role.Key,
            RoleName = u.Role.Name,
            TotalStays = db.StayListings.Count(s => s.UserId == u.Id && !s.IsDeleted),
            StayStatus = db.StayListings.Where(s => s.UserId == u.Id && !s.IsDeleted).Select(s => s.Status).FirstOrDefault(),
            TotalTaxis = db.TaxiListings.Count(t => t.UserId == u.Id && !t.IsDeleted),
            TaxiStatus = db.TaxiListings.Where(t => t.UserId == u.Id && !t.IsDeleted).Select(t => t.Status).FirstOrDefault(),
            Plan = db.UserPlans
                .Where(p => p.UserId == u.Id && p.Status == UserPlanStatuses.Active && p.EndsAt > now)
                .OrderByDescending(p => p.EndsAt)
                .Select(p => new AdminUserPlanSummaryDto { FeatureKey = p.FeatureKey, PlanName = p.Plan.Name, EndsAt = p.EndsAt, Source = p.Source })
                .FirstOrDefault(),
        });

        var result = await projected
            .OrderByDescending(x => x.User.CreatedAt)
            .ToPagedResultAsync(page, pageSize, x =>
            {
                var u = x.User;
                return new AdminUserDto
                {
                    Id = u.Id,
                    PhoneNumber = u.PhoneNumber,
                    IsPhoneVerified = u.IsPhoneVerified,
                    Name = u.Name,
                    IsActive = u.IsActive,
                    CreatedAt = u.CreatedAt,
                    TotalStays = x.TotalStays,
                    StayStatus = x.StayStatus,
                    TotalTaxis = x.TotalTaxis,
                    TaxiStatus = x.TaxiStatus,
                    Plan = x.Plan,
                    Role = x.RoleKey,
                    RoleName = x.RoleName,
                };
            });

        var blocking = await guard.GetBlockingCountsAsync(result.Items.Select(u => u.Id).ToList());
        foreach (var item in result.Items)
        {
            item.LiveStayListings = blocking[item.Id].Stays;
            item.LiveTaxiListings = blocking[item.Id].Taxis;
        }

        return OkResponse(result);
    }

    public static async Task<IResult> GetDeletedAccounts(
        ApplicationDbContext db, IIdentityCipher cipher, ILoggerFactory loggerFactory,
        int page = 1, int pageSize = 20, string? search = null)
    {
        var logger = loggerFactory.CreateLogger(nameof(AdminHandlers));
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.DeletedAccountRecords.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(r =>
                r.PhoneNumber.Contains(s) ||
                (r.Name != null && r.Name.ToLower().Contains(s)));
        }

        var result = await query
            .OrderByDescending(r => r.DeletedAt)
            .ToPagedResultAsync(page, pageSize, r =>
            {
                var dto = r.Adapt<DeletedAccountRecordDto>();
                var licence = TaxiHandlers.ReadLicence(cipher, r.DrivingLicenceNumberEnc, r.Id, logger, deletedRecord: true);
                dto.DrivingLicenceNumber = licence == null ? null : TaxiIdentityRules.FormatDl(licence);
                dto.DateOfBirth = r.DateOfBirth is { } dob ? TaxiIdentityRules.FormatDob(dob) : null;
                return dto;
            });

        return OkResponse(new { items = result.Items, hasMore = result.HasMore });
    }

    public static async Task<IResult> GetUserById(Guid id, ApplicationDbContext db, IRoleChangeGuardService guard)
    {
        var u = await db.Users.AsNoTracking().Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == id);
        if (u == null) return NotFoundResponse("User not found");

        var blocking = (await guard.GetBlockingCountsAsync([id]))[id];

        var dto = new AdminUserDto
        {
            Id = u.Id,
            PhoneNumber = u.PhoneNumber,
            IsPhoneVerified = u.IsPhoneVerified,
            Name = u.Name,
            IsActive = u.IsActive,
            CreatedAt = u.CreatedAt,
            TotalStays = await db.StayListings.CountAsync(s => s.UserId == id && !s.IsDeleted),
            LiveStayListings = blocking.Stays,
            StayStatus = await db.StayListings.Where(s => s.UserId == id && !s.IsDeleted).Select(s => s.Status).FirstOrDefaultAsync(),
            TotalTaxis = await db.TaxiListings.CountAsync(t => t.UserId == id && !t.IsDeleted),
            LiveTaxiListings = blocking.Taxis,
            TaxiStatus = await db.TaxiListings.Where(t => t.UserId == id && !t.IsDeleted).Select(t => t.Status).FirstOrDefaultAsync(),
            Plan = await db.UserPlans.AsNoTracking()
                .Where(p => p.UserId == id && p.Status == UserPlanStatuses.Active && p.EndsAt > DateTime.UtcNow)
                .OrderByDescending(p => p.EndsAt)
                .Select(p => new AdminUserPlanSummaryDto { FeatureKey = p.FeatureKey, PlanName = p.Plan.Name, EndsAt = p.EndsAt, Source = p.Source })
                .FirstOrDefaultAsync(),
            Role = u.Role.Key,
            RoleName = u.Role.Name,
        };

        return OkResponse(dto);
    }

    public static async Task<IResult> UpdateUserStatus(
        Guid id,
        UpdateUserStatusRequest request,
        ApplicationDbContext db,
        ILiveSlotService slots,
        IPlaySubscriptionRepository playSubscriptions,
        IServiceProvider sp)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFoundResponse("User not found");

        var now = DateTime.UtcNow;
        List<Guid?> stayDistrictIds, taxiDistrictIds;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await slots.LockOwnerAsync(id);
            user.IsActive = request.IsActive;
            user.UpdatedAt = now;
            await db.SaveChangesAsync();

            // Stays and taxis have no paid window; an approved one simply follows the owner.
            var stayQuery = db.StayListings.Where(s => s.UserId == id && !s.IsDeleted && s.Status == StayStatuses.Approved);
            stayDistrictIds = await stayQuery.Select(s => s.DistrictId).Distinct().ToListAsync();
            await stayQuery.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.AcceptingGuests, request.IsActive)
                .SetProperty(x => x.UpdatedAt, now));

            var taxiQuery = db.TaxiListings.Where(t => t.UserId == id && !t.IsDeleted && t.Status == TaxiStatuses.Approved);
            taxiDistrictIds = await taxiQuery.Select(t => t.DistrictId).Distinct().ToListAsync();
            await taxiQuery.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsAvailable, request.IsActive)
                .SetProperty(x => x.UpdatedAt, now));

            // Revoke live sessions on suspend so the next request fails OnTokenValidated with 401;
            // re-login is then blocked by the IsActive check in AuthHandlers.
            if (!request.IsActive)
            {
                await db.Sessions
                    .Where(s => s.UserId == id && s.IsActive)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.IsActive, false)
                        .SetProperty(x => x.LoggedOutAt, now));

                // Stop Google renewals so a blocked user is not billed for an account they cannot use; no refund.
                await playSubscriptions.QueueCancelForUserAsync(id, "Account deactivated");
            }

            await tx.CommitAsync();
        }

        var redis = sp.GetService<IConnectionMultiplexer>();
        foreach (var districtId in stayDistrictIds)
            await StayCache.InvalidateAsync(redis, districtId);
        foreach (var districtId in taxiDistrictIds)
            await TaxiCache.InvalidateAsync(redis, districtId);

        return OkResponse(new { id = user.Id, isActive = user.IsActive });
    }

    private static AdminListingReportDto ToAdminListingReportDto(ListingReport r) => new()
    {
        Id = r.Id,
        ListingId = r.ListingId,
        ListingType = r.ListingType,
        ReporterUserId = r.ReporterUserId,
        ReporterName = r.ReporterName,
        ReporterMobile = r.ReporterMobile,
        ReportedUserId = r.ReportedUserId,
        ReportedName = r.ReportedName,
        ReportedMobile = r.ReportedMobile,
        ReasonName = r.Reason?.Name ?? "",
        Details = r.Details,
        Status = r.Status,
        ResolutionAction = r.ResolutionAction,
        CreatedAt = r.CreatedAt,
        ResolvedAt = r.ResolvedAt,
        TripEnquiryId = r.TripEnquiryId,
    };

    private static void ApplyTripEnquiryReportDetails(AdminListingReportDto dto, TripEnquiry e)
    {
        dto.TripEnquiryKind = e.Kind;
        dto.TripEnquiryDates = TripNotifications.Dates(e);
        dto.TripEnquiryStatus = e.Status;
    }

    public static async Task<IResult> GetReports(
        IUnitOfWork unitOfWork, ApplicationDbContext db, int page = 1, int pageSize = 20, string? status = "Pending",
        string? cursor = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        // cursor = keyset (resolve shrinks a Pending queue); page > 1 without a cursor = the old app's offset paging.
        DateTime? afterAt = null;
        Guid? afterId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!KeysetCursor.TryDecode(cursor, out var at, out var id)) return BadRequestResponse("Invalid cursor");
            afterAt = at;
            afterId = id;
        }
        var paged = afterAt == null && page > 1
            ? await unitOfWork.ListingReports.GetPagedAsync(page, pageSize, status)
            : await KeysetReportsAsync(unitOfWork, pageSize, status, afterAt, afterId);
        var nextCursor = KeysetCursor.EncodeLast(paged.Items, paged.HasMore, r => r.CreatedAt, r => r.Id);

        // Batched lookup instead of N+1 — ListingReport has no FK/nav to StayListing/TaxiListing
        // (just a ListingId + ListingType string discriminator), so the underlying listing content
        // has to be joined in manually here.
        var stayIds = paged.Items.Where(r => r.ListingType == ListingKinds.Stay).Select(r => r.ListingId).Distinct().ToList();
        var taxiIds = paged.Items.Where(r => r.ListingType == ListingKinds.Taxi).Select(r => r.ListingId).Distinct().ToList();

        var staysById = stayIds.Count == 0 ? new Dictionary<Guid, StayListing>() : await db.StayListings
            .AsNoTracking().Include(s => s.StayType).Include(s => s.District).Include(s => s.City)
            .Where(s => stayIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var taxisById = taxiIds.Count == 0 ? new Dictionary<Guid, TaxiListing>() : await db.TaxiListings
            .AsNoTracking().Include(t => t.VehicleType).Include(t => t.District).Include(t => t.City)
            .Where(t => taxiIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);

        var tripEnquiryIds = paged.Items.Where(r => r.TripEnquiryId != null).Select(r => r.TripEnquiryId!.Value).Distinct().ToList();
        var tripEnquiriesById = tripEnquiryIds.Count == 0 ? new Dictionary<Guid, TripEnquiry>() : await db.TripEnquiries
            .AsNoTracking().Where(e => tripEnquiryIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id);

        var items = paged.Items.Select(r =>
        {
            var dto = ToAdminListingReportDto(r);
            if (r.TripEnquiryId is { } tripEnquiryId && tripEnquiriesById.TryGetValue(tripEnquiryId, out var tripEnquiry))
                ApplyTripEnquiryReportDetails(dto, tripEnquiry);
            if (r.ListingType == ListingKinds.Stay && staysById.TryGetValue(r.ListingId, out var stay)) AdminStayHandlers.ApplyStayReportDetails(dto, stay);
            else if (r.ListingType == ListingKinds.Taxi && taxisById.TryGetValue(r.ListingId, out var taxi)) AdminTaxiHandlers.ApplyTaxiReportDetails(dto, taxi);
            return dto;
        }).ToList();

        return OkResponse(new { items, hasMore = paged.HasMore, nextCursor });
    }

    private static async Task<PagedResult<ListingReport>> KeysetReportsAsync(
        IUnitOfWork unitOfWork, int pageSize, string? status, DateTime? afterAt, Guid? afterId)
    {
        var (rows, hasMore) = await unitOfWork.ListingReports.GetAfterCursorAsync(pageSize, status, afterAt, afterId);
        return new PagedResult<ListingReport> { Items = rows, Page = 1, PageSize = pageSize, HasMore = hasMore };
    }

    public static async Task<IResult> GetReportById(Guid id, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        var report = await unitOfWork.ListingReports.GetByIdAsync(id);
        if (report == null) return NotFoundResponse("Report not found");
        var dto = ToAdminListingReportDto(report);

        if (report.TripEnquiryId is { } tripEnquiryId
            && await db.TripEnquiries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == tripEnquiryId) is { } tripEnquiry)
            ApplyTripEnquiryReportDetails(dto, tripEnquiry);

        if (report.ListingType == ListingKinds.Stay)
        {
            var stay = await unitOfWork.Stays.GetByIdForAdminAsync(report.ListingId);
            if (stay != null)
            {
                AdminStayHandlers.ApplyStayReportDetails(dto, stay);
                dto.Photos = stay.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).ToList();
            }
        }
        else if (report.ListingType == ListingKinds.Taxi)
        {
            var taxi = await unitOfWork.Taxis.GetByIdForAdminAsync(report.ListingId);
            if (taxi != null)
            {
                AdminTaxiHandlers.ApplyTaxiReportDetails(dto, taxi);
                if (taxi.PlatePhotoUrl != null) dto.Photos = [taxi.PlatePhotoUrl];
            }
        }

        return OkResponse(dto);
    }

    public static async Task<IResult> ResolveReport(
        Guid id, ResolveReportRequest request, IValidator<ResolveReportRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var report = await unitOfWork.ListingReports.GetByIdAsync(id);
        if (report == null) return NotFoundResponse("Report not found");
        if (report.Status != "Pending") return BadRequestResponse("Report has already been resolved");

        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Guid? adminId = Guid.TryParse(adminIdClaim, out var parsedAdminId) ? parsedAdminId : null;

        await unitOfWork.ListingReports.ResolveAsync(id, adminId, request.ResolutionAction);

        return OkResponse(new { success = true });
    }

    // ── Report-driven takedown ("Reject") ──────────────────────────────────────
    // One atomic endpoint (take down + resolve). One HTTP route; report.ListingType picks the Stay or Taxi path.

    public static async Task<IResult> RejectReport(
        Guid id, RejectGoLiveRequest request, IValidator<RejectGoLiveRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var report = await unitOfWork.ListingReports.GetByIdAsync(id);
        if (report == null) return NotFoundResponse("Report not found");
        if (report.Status != "Pending") return BadRequestResponse("Report has already been resolved");

        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Guid? adminId = Guid.TryParse(adminIdClaim, out var parsedAdminId) ? parsedAdminId : null;

        return report.ListingType switch
        {
            ListingKinds.Stay => await AdminStayHandlers.RejectReportedStayAsync(report, request.Reason, adminId, unitOfWork, db, sp, hubContext, publisher),
            ListingKinds.Taxi => await AdminTaxiHandlers.RejectReportedTaxiAsync(report, request.Reason, adminId, unitOfWork, db, sp, hubContext, publisher),
            ListingReportTypes.TripEnquiry => BadRequestResponse("A traveller report has no listing to take down"),
            _ => BadRequestResponse($"Unknown listing type '{report.ListingType}' on this report"),
        };
    }

    // Single-notification push shape — SignalR
    // user_{id} push via NotificationHub (the same hub every other generic NotificationEvent producer
    // reuses) + RabbitMQ notification.push publish for NotificationPushWorkerService. Best-effort:
    // a push failure must never turn an already-committed Approve/Reject into an error response.
    internal static async Task SendGoLiveNotificationAsync(
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher, NotificationEvent notification,
        ILogger? logger = null)
    {
        try
        {
            await hubContext.Clients.Group($"user_{notification.TargetUserId}").SendAsync("NotificationReceived", new
            {
                id = notification.Id,
                type = notification.Type,
                title = notification.Title,
                body = notification.Body,
                actionRoute = notification.ActionRoute,
                actionArguments = NotificationDto.ParseArguments(notification.ActionArgumentsJson),
            });
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "notify.hub_failed {NotificationId} {Type}", notification.Id, notification.Type);
        }

        try
        {
            var payload = JsonSerializer.Serialize(new NotificationPushPayload(notification.Id, notification.TargetUserId!.Value));
            await publisher.PublishAsync("notification.push", payload);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "notify.publish_failed {NotificationId} {Type}", notification.Id, notification.Type);
        }
    }
}
