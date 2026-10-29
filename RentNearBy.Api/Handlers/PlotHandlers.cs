using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Mapster;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Extensions;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class PlotListingHandlers
{
    private static readonly string[] AllowedAreaUnits = ["sqft", "bigha", "acre", "nali"];
    private static readonly TimeSpan ReportSubmitWindow = TimeSpan.FromHours(1);
    private const int ReportSubmitMax = 5;

    private static decimal ToSqft(decimal value, string unit) => unit switch
    {
        "sqft"  => value,
        "bigha" => value * 27000m,
        "acre"  => value * 43560m,
        "nali"  => value * 2152.78m,
        _       => value
    };

    private static readonly TimeSpan ContextCacheTtl = TimeSpan.FromMinutes(10);
    private static string ContextCacheKey(double lat, double lng) => $"plot_context:{lat:F2}:{lng:F2}";

    private static readonly TimeSpan NearbyCacheTtl = TimeSpan.FromSeconds(60);
    private static string NearbyCacheKey(Guid districtId, double radius, double lat, double lng)
        => $"nearby_plot:{districtId}:{radius:F1}:{lat:F3}:{lng:F3}";
    private static string NearbyDistrictPattern(Guid districtId) => $"nearby_plot:{districtId}:*";

    private static readonly TimeSpan NearestCacheTtl = TimeSpan.FromSeconds(60);
    private static string NearestCacheKey(Guid districtId, int count, double lat, double lng)
        => $"nearest_plot:{districtId}:{count}:{lat:F3}:{lng:F3}";
    private static string NearestDistrictPattern(Guid districtId) => $"nearest_plot:{districtId}:*";

    private static async Task InvalidateNearbyCacheAsync(IConnectionMultiplexer? redis, Guid districtId)
    {
        if (redis == null) return;
        try
        {
            var db = redis.GetDatabase();
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
            if (server == null) return;
            foreach (var pattern in new[] { NearbyDistrictPattern(districtId), NearestDistrictPattern(districtId) })
                await foreach (var key in server.KeysAsync(pattern: pattern))
                    await db.KeyDeleteAsync(key);
        }
        catch { }
    }

    // Home's "Recently added" feed (HomeHandlers.GetRecentPlots) is district-free and cached under
    // this one fixed key — unlike nearby-cache above, a plot leaving the active/non-deleted set must
    // bust it immediately rather than riding out the cache TTL, since a stale entry here is a
    // tappable card whose detail page 404s, not just a cosmetically-stale count.
    private static async Task InvalidateRecentPlotsCacheAsync(IConnectionMultiplexer? redis)
    {
        if (redis == null) return;
        try { await redis.GetDatabase().KeyDeleteAsync("home:recentPlots"); } catch { }
    }

    // Home's "Plots for you" feed (HomeHandlers.GetPlots) is cached per-district — same
    // immediate-bust reasoning as InvalidateRecentPlotsCacheAsync above, just keyed by district.
    private static async Task InvalidateForYouPlotsCacheAsync(IConnectionMultiplexer? redis, Guid districtId)
    {
        if (redis == null) return;
        try { await redis.GetDatabase().KeyDeleteAsync($"home:forYouPlots:{districtId}"); } catch { }
    }

    private static double Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0;
        var dlat = (lat2 - lat1) * Math.PI / 180;
        var dlng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dlat / 2) * Math.Sin(dlat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dlng / 2) * Math.Sin(dlng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static async Task<IResult> GetContext(double lat, double lng, IServiceProvider sp)
    {
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
            return BadRequestResponse("Invalid coordinates");

        var redis = sp.GetService<IConnectionMultiplexer>();
        var cacheKey = ContextCacheKey(lat, lng);

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(cacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var element = JsonSerializer.Deserialize<JsonElement>(cached!);
                    return OkResponse(element);
                }
                catch (JsonException) { }
            }
        }

        var db = sp.GetRequiredService<ApplicationDbContext>();
        var point = new NetTopologySuite.Geometries.Point(lng, lat) { SRID = 4326 };

        var match = await db.Districts
            .AsNoTracking()
            .Include(d => d.Cities.OrderBy(c => c.Name))
            .FirstOrDefaultAsync(d => d.IsActive && d.Boundary != null && d.Boundary.Contains(point));

        if (match == null)
            return NotFoundResponse("No district found for given coordinates");

        var cities = match.Cities.ToList();
        var nearestCity = cities
            .Where(c => c.Latitude.HasValue && c.Longitude.HasValue)
            .Select(c => new { City = c, Dist = Haversine(lat, lng, (double)c.Latitude!, (double)c.Longitude!) })
            .MinBy(x => x.Dist)?.City ?? cities.FirstOrDefault();

        var result = new
        {
            district = new { id = match.Id, name = match.Name, stateName = match.StateName },
            nearestCityId = nearestCity?.Id,
            cities = cities.Select(c => new { id = c.Id, districtId = c.DistrictId, name = c.Name, latitude = c.Latitude, longitude = c.Longitude }).ToList(),
        };

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(result);
            try { await redis.GetDatabase().StringSetAsync(cacheKey, json, ContextCacheTtl, When.NotExists); } catch { }
        }

        return OkResponse(result);
    }

    public static async Task<IResult> GetNearby(
        double latitude, double longitude, double radius, Guid districtId,
        IUnitOfWork unitOfWork,
        ClaimsPrincipal principal,
        IServiceProvider sp)
    {
        if (radius < 1 || radius > 50)
            return BadRequestResponse("Radius must be between 1 and 50 km");
        if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180)
            return BadRequestResponse("Invalid coordinates");

        var redis = sp.GetService<IConnectionMultiplexer>();
        var isAuth = principal.Identity?.IsAuthenticated == true;
        var cacheKey = NearbyCacheKey(districtId, radius, latitude, longitude);

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(cacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var items = JsonSerializer.Deserialize<List<NearbyPlotListingDto>>(cached!);
                    if (items != null)
                    {
                        if (!isAuth) items.ForEach(d => { d.OwnerName = null; d.OwnerPhone = null; });
                        return OkResponse(new { items });
                    }
                }
                catch (JsonException) { }
            }
        }

        var fetched = (await unitOfWork.PlotListings.GetNearbyAsync(latitude, longitude, radius, districtId)).ToList();

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(fetched);
            try { await redis.GetDatabase().StringSetAsync(cacheKey, json, NearbyCacheTtl, When.NotExists); } catch { }
        }

        if (!isAuth) fetched.ForEach(d => { d.OwnerName = null; d.OwnerPhone = null; });
        return OkResponse(new { items = fetched });
    }

    private static readonly TimeSpan NearestRateLimitWindow = TimeSpan.FromMinutes(10);
    private const int NearestRateLimitMax = 20;

    private static string ClientIp(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static async Task<IResult> GetNearest(
        double latitude, double longitude, Guid districtId,
        IUnitOfWork unitOfWork,
        ClaimsPrincipal principal,
        IServiceProvider sp,
        IRateLimitService rateLimiter,
        HttpContext httpContext,
        int count = 5)
    {
        if (count < 1 || count > 10)
            return BadRequestResponse("Count must be between 1 and 10");
        if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180)
            return BadRequestResponse("Invalid coordinates");

        var ip = ClientIp(httpContext);
        var rl = await rateLimiter.CheckAsync($"nearest:ip:{ip}", NearestRateLimitMax, NearestRateLimitWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var redis = sp.GetService<IConnectionMultiplexer>();
        var isAuth = principal.Identity?.IsAuthenticated == true;
        var cacheKey = NearestCacheKey(districtId, count, latitude, longitude);

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(cacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var items = JsonSerializer.Deserialize<List<NearbyPlotListingDto>>(cached!);
                    if (items != null)
                    {
                        if (!isAuth) items.ForEach(d => { d.OwnerName = null; d.OwnerPhone = null; });
                        return OkResponse(new { items });
                    }
                }
                catch (JsonException) { }
            }
        }

        var fetched = (await unitOfWork.PlotListings.GetNearestAsync(latitude, longitude, count, districtId)).ToList();

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(fetched);
            try { await redis.GetDatabase().StringSetAsync(cacheKey, json, NearestCacheTtl, When.NotExists); } catch { }
        }

        if (!isAuth) fetched.ForEach(d => { d.OwnerName = null; d.OwnerPhone = null; });
        return OkResponse(new { items = fetched });
    }

    public static async Task<IResult> GetById(Guid id, IUnitOfWork unitOfWork, ClaimsPrincipal principal)
    {
        var plot = await unitOfWork.PlotListings.GetByIdWithPhotosAsync(id);
        if (plot == null) return NotFoundResponse("PlotListing not found");
        var dto = plot.Adapt<PlotListingDto>();
        if (principal.Identity?.IsAuthenticated != true)
        {
            dto.OwnerPhone = null;
        }
        else if (UsersHandlers.TryGetUserId(principal, out var userId))
        {
            dto.HasReported = await unitOfWork.ListingReports.HasPendingReportFromReporterAsync(id, "Plot", userId);
        }
        return OkResponse(dto);
    }

    // Anonymous, no-auth lookup for the public website (bakhli.com/l/p/{slug}) and the app's
    // deep-link resolver (/go/p/{slug}) — mirrors GetById's pattern but returns PublicPlotListingDto,
    // which excludes OwnerPhone/UserId/HasReported/PendingReportCount/LiveRequestStatus/RejectedReason
    // at the DTO level, so (unlike GetById) there is no auth-conditional redaction step needed here.
    public static async Task<IResult> GetBySlug(string slug, IUnitOfWork unitOfWork)
    {
        var plot = await unitOfWork.PlotListings.GetBySlugWithPhotosAsync(slug);
        if (plot == null) return NotFoundResponse("PlotListing not found");
        return OkResponse(plot.Adapt<PublicPlotListingDto>());
    }

    public static async Task<IResult> GetMyPlotListings(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 10)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();
        if (pageSize < 1 || pageSize > 50) pageSize = 10;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.PlotListings.GetByUserIdPagedAsync(userId, page, pageSize);
        var dtos = items.Select(p => p.Adapt<PlotListingDto>()).ToList();
        var counts = await unitOfWork.ListingReports.GetPendingCountsForListingsAsync(dtos.Select(d => d.Id), "Plot");
        foreach (var d in dtos) d.PendingReportCount = counts.GetValueOrDefault(d.Id);
        return OkResponse(new { items = dtos, hasMore });
    }

    public static async Task<IResult> GetPlotListingReports(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 20)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.PlotListings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("PlotListing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        pageSize = Math.Clamp(pageSize, 1, 50);
        page = Math.Max(1, page);
        var paged = await unitOfWork.ListingReports.GetPagedForListingAsync(id, "Plot", page, pageSize);
        var items = paged.Items.Select(ToListingReportDto).ToList();
        return OkResponse(new { items, hasMore = paged.HasMore });
    }

    private static ListingReportDto ToListingReportDto(ListingReport r) => new()
    {
        Id = r.Id,
        ListingId = r.ListingId,
        ListingType = r.ListingType,
        ReasonName = r.Reason?.Name ?? "",
        Details = r.Details,
        Status = r.Status,
        ResolutionAction = r.ResolutionAction,
        CreatedAt = r.CreatedAt,
        ResolvedAt = r.ResolvedAt,
    };

    public static async Task<IResult> CreatePlotListing(
        CreatePlotListingRequest request,
        ClaimsPrincipal principal,
        IValidator<CreatePlotListingRequest> validator,
        IUnitOfWork unitOfWork,
        ILiveSlotService slots,
        IPlanEntitlementService entitlements,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var plotType = await unitOfWork.PlotTypes.GetByIdAsync(request.PlotTypeId);
        if (plotType == null) return BadRequestResponse("Invalid plot type");

        // Needed for the slug base (type + locality) below, not just validation — District otherwise
        // never gets an existence check today (see CLAUDE.md's "district validation is not enforced
        // server-side" note; this closes that gap as a side effect of needing the entity's Name).
        var district = await unitOfWork.Districts.GetByIdAsync(request.DistrictId);
        if (district == null) return BadRequestResponse("Invalid district");

        City? city = null;
        if (request.CityId.HasValue)
        {
            city = await unitOfWork.Cities.GetByIdAsync(request.CityId.Value);
            if (city == null) return BadRequestResponse("Selected city does not exist");
            if (city.DistrictId != request.DistrictId)
                return BadRequestResponse("Selected city does not belong to the selected district");
        }

        var plot = new PlotListing
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AreaValue = request.AreaValue,
            AreaUnit = request.AreaUnit,
            AreaSqft = ToSqft(request.AreaValue, request.AreaUnit),
            PlotTypeId = request.PlotTypeId,
            Description = request.Description,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Address = request.Address,
            DistrictId = request.DistrictId,
            CityId = request.CityId,
            IsActive = false, // always — POST /{id}/go-live is now the only path to activation
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Slug generated once here, immutable for the plot's lifetime (see the field comment on
        // PlotListing.Slug) — base is type + locality only, never price/status. Uniqueness is handled
        // by SlugGenerator.GenerateUniqueSlugWithRetryAsync (shared with RoomListingsHandlers.CreateListing).
        var baseSlug = SlugGenerator.Generate($"{plotType.Name} {city?.Name ?? district.Name}");
        // The plan limit (used = not deleted, not Rejected) is checked under the owner lock in the same transaction.
        await unitOfWork.PlotListings.AddAsync(plot);
        var outcome = await ListingSlotHelpers.InsertWithinLimitAsync(
            unitOfWork, slots, entitlements, userId, ListingKinds.Plot, baseSlug,
            slug => plot.Slug = slug,
            limits =>
            {
                plot.PriorityRank = limits.PriorityRank;
                plot.IsFeatured = limits.IsFeatured;
            });
        if (outcome.Limit is { } limit)
            return GoLiveHandlers.ListingLimitReached(limit, outcome.Used ?? limit);
        if (!outcome.Inserted)
            return ConflictResponse("Could not generate a unique link for this plot. Please try again.", "SLUG_GENERATION_FAILED");

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, plot.DistrictId);

        return CreatedResponse(new { plotId = plot.Id }, $"/api/v1/plots/{plot.Id}");
    }

    public static async Task<IResult> UpdatePlotListing(
        Guid id, UpdatePlotListingRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdatePlotListingRequest> validator,
        IUnitOfWork unitOfWork,
        ILiveSlotService slots,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var plot = await unitOfWork.PlotListings.GetByIdAsync(id);
        if (plot == null) return NotFoundResponse("PlotListing not found");
        if (plot.UserId != userId) return ForbiddenResponse("You do not own this plot");

        var oldDistrictId = plot.DistrictId;

        if (request.PlotTypeId.HasValue)
        {
            var newPlotType = await unitOfWork.PlotTypes.GetByIdAsync(request.PlotTypeId.Value);
            if (newPlotType == null) return BadRequestResponse("Invalid plot type");
            plot.PlotTypeId = request.PlotTypeId.Value;
        }
        if (request.AreaValue.HasValue) plot.AreaValue = request.AreaValue.Value;
        if (request.AreaUnit != null) plot.AreaUnit = request.AreaUnit;
        if (request.AreaValue.HasValue || request.AreaUnit != null)
            plot.AreaSqft = ToSqft(plot.AreaValue, plot.AreaUnit);
        if (request.Description != null) plot.Description = request.Description;
        if (request.Latitude.HasValue) plot.Latitude = request.Latitude.Value;
        if (request.Longitude.HasValue) plot.Longitude = request.Longitude.Value;
        if (request.Address != null) plot.Address = request.Address;
        if (request.CityId.HasValue)
        {
            var city = await unitOfWork.Cities.GetByIdAsync(request.CityId.Value);
            if (city == null) return BadRequestResponse("Selected city does not exist");
            plot.CityId = request.CityId.Value;
            plot.DistrictId = city.DistrictId;
        }
        var freesSlot = false;
        if (request.IsActive.HasValue)
        {
            if (request.IsActive.Value)
                return BadRequestResponse("Use POST /plots/{id}/go-live to activate a plot.");
            if (plot.IsActive)
            {
                // Owner pause: never auto-resumed; Go Live within the limit brings it back. Its slot goes to
                // the newest plan-paused row.
                freesSlot = plot.LiveRequestStatus != GoLiveRequestStatuses.Pending;
                plot.IsActive = false;
                plot.PauseReason = PauseReasons.Owner;
            }
        }
        plot.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.PlotListings.UpdateAsync(plot);
        var resumed = 0;
        if (freesSlot)
        {
            await unitOfWork.BeginTransactionAsync();
            try
            {
                await slots.LockOwnerAsync(userId);
                try
                {
                    await unitOfWork.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    await unitOfWork.RollbackTransactionAsync();
                    return ConflictResponse("This plot was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
                }
                resumed = await slots.ResumeAfterSlotFreedAsync(userId, ListingKinds.Plot);
                await unitOfWork.CommitTransactionAsync();
            }
            catch
            {
                await unitOfWork.RollbackTransactionAsync();
                throw;
            }
        }
        else
        {
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return ConflictResponse("This plot was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
        }

        if (request.IsActive.HasValue && request.IsActive.Value == false)
            await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(id, "Plot");

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, plot.DistrictId);
        await InvalidateForYouPlotsCacheAsync(redis, plot.DistrictId);
        if (oldDistrictId != plot.DistrictId)
        {
            await InvalidateNearbyCacheAsync(redis, oldDistrictId);
            await InvalidateForYouPlotsCacheAsync(redis, oldDistrictId);
        }
        if (request.IsActive.HasValue && request.IsActive.Value == false)
            await InvalidateRecentPlotsCacheAsync(redis);
        if (resumed > 0)
            await sp.GetRequiredService<IListingCacheInvalidator>().BustForUserAsync(userId, ListingKinds.Plot);

        return OkResponse(new { success = true });
    }

    public static async Task<IResult> DeletePlotListing(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork,
        IPhotoService photoService, ILiveSlotService slots, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var plot = await unitOfWork.PlotListings.GetByIdWithPhotosAsync(id);
        if (plot == null) return NotFoundResponse("PlotListing not found");
        if (plot.UserId != userId) return ForbiddenResponse("You do not own this plot");

        var districtId = plot.DistrictId;

        foreach (var photo in plot.Photos)
            await photoService.DeletePhotoAsync(photo.FilePath);

        // GetByIdWithPhotosAsync is AsNoTracking + Include()-heavy, so the delete flag goes on a plain
        // tracked re-fetch, taken under the owner lock right before the save.
        // Deleting a live (or pending) row frees a slot: the newest plan-paused row resumes in the same transaction.
        var resumed = 0;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(userId);
            var trackedPlot = await unitOfWork.PlotListings.GetByIdAsync(id);
            if (trackedPlot == null)
            {
                await unitOfWork.RollbackTransactionAsync();
                return NotFoundResponse("PlotListing not found");
            }
            var wasLive = trackedPlot.IsActive || trackedPlot.LiveRequestStatus == GoLiveRequestStatuses.Pending;
            trackedPlot.IsDeleted = true;
            trackedPlot.DeletedAt = DateTime.UtcNow;
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This plot was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            if (wasLive)
                resumed = await slots.ResumeAfterSlotFreedAsync(userId, ListingKinds.Plot);
            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(id, "Plot");

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, districtId);
        await InvalidateRecentPlotsCacheAsync(redis);
        await InvalidateForYouPlotsCacheAsync(redis, districtId);
        if (resumed > 0)
            await sp.GetRequiredService<IListingCacheInvalidator>().BustForUserAsync(userId, ListingKinds.Plot);

        return NoContentResponse();
    }

    // Makes a paused, approved listing live in place of a live one (owner lock, both rows xmin-guarded).
    public static async Task<IResult> SwapLive(
        Guid id,
        SwapLiveRequest request,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        ILiveSlotService slots,
        IPlanEntitlementService entitlements,
        IListingCacheInvalidator cacheInvalidator)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();
        if (request == null || request.PauseListingId == Guid.Empty || request.PauseListingId == id)
            return BadRequestResponse("Choose a live listing to pause.", PlanErrorTypes.SwapInvalid);

        int limit, live;
        var districts = new List<Guid>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(userId);
            var target = await unitOfWork.PlotListings.GetByIdAsync(id);
            var other = await unitOfWork.PlotListings.GetByIdAsync(request.PauseListingId);
            if (target == null || other == null || target.IsDeleted || other.IsDeleted
                || target.UserId != userId || other.UserId != userId
                || target.IsActive || target.LiveRequestStatus != GoLiveRequestStatuses.Approved
                || target.PauseReason is PauseReasons.Admin or PauseReasons.Suspended
                || !other.IsActive)
            {
                await unitOfWork.RollbackTransactionAsync();
                return BadRequestResponse("These listings cannot be swapped.", PlanErrorTypes.SwapInvalid);
            }

            var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Plot);
            // Swap only at the limit: with a free slot, plan-pausing `other` would strand it next to an empty slot.
            if (await slots.CountLiveAsync(userId, ListingKinds.Plot, excludeId: target.Id) < limits.ListingLimit)
            {
                await unitOfWork.RollbackTransactionAsync();
                return BadRequestResponse("A slot is free. Use Go Live instead.", PlanErrorTypes.SwapInvalid);
            }
            var now = DateTime.UtcNow;
            other.IsActive = false;
            other.PauseReason = PauseReasons.Plan;
            other.UpdatedAt = now;
            target.IsActive = true;
            target.PauseReason = null;
            target.ValidUntil = entitlements.ResolveValidUntil(limits);
            target.PriorityRank = limits.PriorityRank;
            target.IsFeatured = limits.IsFeatured;
            target.UpdatedAt = now;
            await unitOfWork.PlotListings.UpdateAsync(other);
            await unitOfWork.PlotListings.UpdateAsync(target);
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            limit = limits.ListingLimit;
            live = await slots.CountLiveAsync(userId, ListingKinds.Plot);
            await unitOfWork.CommitTransactionAsync();
            districts.Add(target.DistrictId);
            districts.Add(other.DistrictId);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await cacheInvalidator.BustDistrictsAsync(ListingKinds.Plot, districts);
        return OkResponse(new { limits = new LiveLimitsDto { ListingLimit = limit, Live = live } });
    }

    public static async Task<IResult> ReportPlotListing(
        Guid id, CreateListingReportRequest request,
        ClaimsPrincipal principal,
        IValidator<CreateListingReportRequest> validator,
        IUnitOfWork unitOfWork, IRabbitMqPublisher publisher,
        IRateLimitService rateLimiter, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        // Shared per-user budget with Room reports (same key namespace) — the limit is
        // on report-spamming behavior overall, not per listing type.
        var reportRl = await rateLimiter.CheckAsync($"report:submit:{userId}", ReportSubmitMax, ReportSubmitWindow);
        if (!reportRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)reportRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var plot = await unitOfWork.PlotListings.GetByIdAsync(id);
        if (plot == null) return NotFoundResponse("PlotListing not found");
        if (plot.UserId == userId) return BadRequestResponse("You cannot report your own listing");

        var reason = await unitOfWork.ReportReasons.GetByIdAsync(request.ReasonId);
        if (reason == null) return BadRequestResponse("Invalid reason");

        var reporter = await unitOfWork.Users.GetByIdAsync(userId);
        var owner = await unitOfWork.Users.GetByIdAsync(plot.UserId);

        var isFirstPending = !await unitOfWork.ListingReports.HasPendingReportForListingAsync(id, "Plot");

        var report = new ListingReport
        {
            Id = Guid.NewGuid(),
            ListingId = id,
            ListingType = "Plot",
            ReporterUserId = userId,
            ReporterName = reporter?.Name ?? "Unknown",
            ReporterMobile = reporter?.PhoneNumber ?? "",
            ReportedUserId = plot.UserId,
            ReportedName = owner?.Name ?? "Unknown",
            ReportedMobile = owner?.PhoneNumber ?? "",
            ReasonId = request.ReasonId,
            Details = request.Details,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.ListingReports.AddAsync(report);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ConflictResponse("You have already reported this listing");
        }

        var message = new ReportFiledMessage
        {
            OwnerId = plot.UserId,
            ListingId = plot.Id,
            ListingType = "Plot",
            ReasonName = reason.Name,
            ListingTitle = plot.Address ?? "your listing",
            NotifyOwner = isFirstPending,
        };
        try { await publisher.PublishAsync("report.filed", JsonSerializer.Serialize(message)); }
        catch (Exception) { /* best-effort — report row is already saved regardless of push delivery */ }

        return CreatedResponse(new { reportId = report.Id }, $"/api/v1/admin/reports/{report.Id}");
    }

    public static async Task<IResult> UploadPhoto(
        Guid id, IFormFile photo,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork,
        IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var plot = await unitOfWork.PlotListings.GetByIdWithPhotosAsync(id);
        if (plot == null) return NotFoundResponse("PlotListing not found");
        if (plot.UserId != userId) return ForbiddenResponse("You do not own this plot");
        if (plot.Photos.Count >= 5) return BadRequestResponse("Maximum 5 photos allowed per plot");
        if (photo.Length > 10 * 1024 * 1024) return BadRequestResponse("Photo size must not exceed 10MB");

        using var stream = photo.OpenReadStream();
        if (!IsValidImageMagicBytes(stream)) return BadRequestResponse("File must be a valid image (JPEG, PNG or WebP)");
        stream.Position = 0;
        var (url, filePath) = await photoService.SavePhotoAsync(stream, photo.FileName, userId, id);

        var plotPhoto = new PlotPhoto
        {
            Id = Guid.NewGuid(),
            PlotId = id,
            PhotoUrl = url,
            FilePath = filePath,
            PhotoOrder = plot.Photos.Count,
            UploadedAt = DateTime.UtcNow
        };

        await unitOfWork.PlotListings.AddPhotoAsync(plotPhoto);
        await unitOfWork.SaveChangesAsync();

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, plot.DistrictId);
        await InvalidateForYouPlotsCacheAsync(redis, plot.DistrictId);

        return CreatedResponse(new { photoUrl = url, photoId = plotPhoto.Id }, url);
    }

    public static async Task<IResult> DeletePhoto(
        Guid id, Guid photoId,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork,
        IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var plot = await unitOfWork.PlotListings.GetByIdWithPhotosAsync(id);
        if (plot == null) return NotFoundResponse("PlotListing not found");
        if (plot.UserId != userId) return ForbiddenResponse("You do not own this plot");

        var photo = plot.Photos.FirstOrDefault(p => p.Id == photoId);
        if (photo == null) return NotFoundResponse("Photo not found");

        await photoService.DeletePhotoAsync(photo.FilePath);
        unitOfWork.PlotListings.RemovePhoto(photo);
        await unitOfWork.SaveChangesAsync();

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, plot.DistrictId);
        await InvalidateForYouPlotsCacheAsync(redis, plot.DistrictId);

        return NoContentResponse();
    }

    private static bool IsValidImageMagicBytes(Stream stream)
    {
        var header = new byte[12];
        var read = stream.Read(header, 0, header.Length);
        if (read < 3) return false;
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return true;
        if (read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return true;
        if (read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
            && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50) return true;
        return false;
    }

    // ── Admin PlotListing endpoints ───────────────────────────────────────────────────

    public record AdminTogglePlotListingRequest(bool IsActive);

    public record AdminPlotListingDto(
        string Id, string UserId, string? OwnerName, string? OwnerPhone,
        string PlotType, double AreaValue, string AreaUnit, double AreaSqft,
        bool IsActive, DateTime? ValidUntil, string? DistrictName, string? CityName, string? Address,
        string? ThumbnailUrl, int PhotoCount, DateTime CreatedAt,
        string? PauseReason, short PriorityRank, bool IsFeatured);

    public static async Task<IResult> GetAdminPlotListings(
        IUnitOfWork unitOfWork,
        int page = 1, int pageSize = 20,
        string? plotType = null, bool? isActive = null, Guid? districtId = null, Guid? cityId = null,
        string? search = null)
    {
        if (pageSize < 1 || pageSize > 100) pageSize = 20;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.PlotListings.GetAllAsync(page, pageSize, plotType, isActive, districtId, cityId, search);
        var dtos = items.Select(p => new AdminPlotListingDto(
            Id: p.Id.ToString(),
            UserId: p.UserId.ToString(),
            OwnerName: p.User?.Name,
            OwnerPhone: p.User?.PhoneNumber,
            PlotType: p.PlotType?.Name ?? string.Empty,
            AreaValue: (double)p.AreaValue,
            AreaUnit: p.AreaUnit,
            AreaSqft: (double)p.AreaSqft,
            IsActive: p.IsActive,
            ValidUntil: p.ValidUntil,
            DistrictName: p.District?.Name,
            CityName: p.City?.Name,
            Address: p.Address,
            ThumbnailUrl: p.Photos.FirstOrDefault()?.PhotoUrl,
            PhotoCount: p.Photos.Count,
            CreatedAt: p.CreatedAt,
            PauseReason: p.PauseReason,
            PriorityRank: p.PriorityRank,
            IsFeatured: p.IsFeatured
        )).ToList();

        return OkResponse(new { items = dtos, hasMore });
    }

    public static async Task<IResult> GetAdminPlotById(Guid id, IUnitOfWork unitOfWork)
    {
        var plot = await unitOfWork.PlotListings.GetByIdWithPhotosForAdminAsync(id);
        if (plot == null) return NotFoundResponse("PlotListing not found");
        return OkResponse(plot.Adapt<PlotListingDto>());
    }

    // Deactivate = admin pause (owner cannot self-resume). Reactivate needs a valid ValidUntil and a free
    // slot in the owner's plan, checked under the owner lock.
    public static async Task<IResult> AdminTogglePlotListing(
        Guid id, AdminTogglePlotListingRequest request,
        IUnitOfWork unitOfWork, ILiveSlotService slots, IPlanEntitlementService entitlements, IServiceProvider sp)
    {
        var probe = await unitOfWork.PlotListings.GetByIdAsync(id);
        if (probe == null || probe.IsDeleted) return NotFoundResponse("PlotListing not found");
        var ownerId = probe.UserId;

        var resumed = 0;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(ownerId);
            var plot = probe;
            var freesSlot = !request.IsActive && plot.IsActive && plot.LiveRequestStatus != GoLiveRequestStatuses.Pending;
            if (request.IsActive)
            {
                if (!plot.IsActive)
                {
                    if (!(plot.ValidUntil > DateTime.UtcNow))
                    {
                        await unitOfWork.RollbackTransactionAsync();
                        return BadRequestResponse("This plot has no valid live period. Ask the owner to Go Live.");
                    }
                    var limits = await entitlements.GetLimitsAsync(ownerId, ListingKinds.Plot);
                    var live = await slots.CountLiveAsync(ownerId, ListingKinds.Plot, plot.Id);
                    if (live >= limits.ListingLimit)
                    {
                        await unitOfWork.RollbackTransactionAsync();
                        return GoLiveHandlers.PlanLimitReached(limits.ListingLimit, live);
                    }
                    plot.IsActive = true;
                    plot.PauseReason = null;
                    plot.PriorityRank = limits.PriorityRank;
                    plot.IsFeatured = limits.IsFeatured;
                }
            }
            else if (plot.IsActive || plot.PauseReason != PauseReasons.Admin)
            {
                plot.IsActive = false;
                plot.PauseReason = PauseReasons.Admin;
            }
            plot.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.PlotListings.UpdateAsync(plot);
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This plot was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            if (freesSlot)
                resumed = await slots.ResumeAfterSlotFreedAsync(ownerId, ListingKinds.Plot);
            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, probe.DistrictId);
        await InvalidateRecentPlotsCacheAsync(redis);
        await InvalidateForYouPlotsCacheAsync(redis, probe.DistrictId);
        if (resumed > 0)
            await sp.GetRequiredService<IListingCacheInvalidator>().BustForUserAsync(ownerId, ListingKinds.Plot);

        return OkResponse(new { success = true, isActive = probe.IsActive, pauseReason = probe.PauseReason });
    }

    public static async Task<IResult> AdminDeletePlotListing(
        Guid id, IUnitOfWork unitOfWork,
        IPhotoService photoService, ILiveSlotService slots, IServiceProvider sp)
    {
        var plot = await unitOfWork.PlotListings.GetByIdWithPhotosAsync(id);
        if (plot == null || plot.IsDeleted) return NotFoundResponse("PlotListing not found");

        var districtId = plot.DistrictId;
        var ownerId = plot.UserId;

        foreach (var photo in plot.Photos)
            await photoService.DeletePhotoAsync(photo.FilePath);

        // GetByIdWithPhotosAsync is AsNoTracking + Include()-heavy, so the delete flag goes on a plain
        // tracked re-fetch, taken under the owner lock right before the save.
        var resumed = 0;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(ownerId);
            var trackedPlot = await unitOfWork.PlotListings.GetByIdAsync(id);
            if (trackedPlot == null)
            {
                await unitOfWork.RollbackTransactionAsync();
                return NotFoundResponse("PlotListing not found");
            }
            var wasLive = trackedPlot.IsActive || trackedPlot.LiveRequestStatus == GoLiveRequestStatuses.Pending;
            trackedPlot.IsDeleted = true;
            trackedPlot.DeletedAt = DateTime.UtcNow;
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This plot was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            if (wasLive)
                resumed = await slots.ResumeAfterSlotFreedAsync(ownerId, ListingKinds.Plot);
            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, districtId);
        await InvalidateRecentPlotsCacheAsync(redis);
        await InvalidateForYouPlotsCacheAsync(redis, districtId);
        if (resumed > 0)
            await sp.GetRequiredService<IListingCacheInvalidator>().BustForUserAsync(ownerId, ListingKinds.Plot);

        return NoContentResponse();
    }

    // ── Plot Go-Live moderation ───────────────────────────────────────────────
    // Mirrors AdminHandlers' Room Go-Live request quartet (GetRoomGoLiveRequests/
    // GetRoomGoLiveRequestById/ApproveRoomGoLiveRequest/RejectRoomGoLiveRequest) exactly, kept as a
    // separate Plot-only pair per the plan's Room/Plot-duplication convention (these fields live
    // directly on PlotListing, so a unified feed would need a cross-entity UNION for no real benefit).

    private static GoLiveRequestDto ToGoLiveRequestDto(PlotListing p, bool includePhotos) => new()
    {
        Id = p.Id,
        UserId = p.UserId,
        OwnerName = p.User?.Name,
        OwnerMobile = p.User?.PhoneNumber,
        Status = p.LiveRequestStatus ?? string.Empty,
        RequestedPlanDays = p.RequestedPlanDays,
        SubmittedAt = p.UpdatedAt,
        Photos = includePhotos ? p.Photos.Select(ph => ph.PhotoUrl).ToList() : new List<string>(),
        Address = p.Address,
        Description = p.Description,
        DistrictName = p.District?.Name,
        CityName = p.City?.Name,
        PlotType = p.PlotType?.Name,
        AreaValue = p.AreaValue,
        AreaUnit = p.AreaUnit,
        AreaSqft = p.AreaSqft,
    };

    public static async Task<IResult> GetPlotGoLiveRequests(
        ApplicationDbContext db, IPlanEntitlementService entitlements, int page = 1, int pageSize = 20, string? status = "Pending")
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        // Deleted plots never leave LiveRequestStatus behind — excluded here so a
        // deleted-while-Pending plot can't linger in this queue forever.
        var query = db.PlotListings
            .AsNoTracking()
            .Include(p => p.User)
            .Include(p => p.PlotType)
            .Include(p => p.District)
            .Include(p => p.City)
            .Where(p => !p.IsDeleted && p.LiveRequestStatus == status)
            .OrderByDescending(p => p.UpdatedAt);

        var result = await query.ToPagedResultAsync(page, pageSize, p => ToGoLiveRequestDto(p, includePhotos: false));
        await FillOwnerPlanNamesAsync(entitlements, result.Items);
        return OkResponse(new { items = result.Items, hasMore = result.HasMore });
    }

    // One UserPlans query for the page.
    private static async Task FillOwnerPlanNamesAsync(IPlanEntitlementService entitlements, List<GoLiveRequestDto> items)
    {
        if (items.Count == 0) return;
        var limits = await entitlements.GetLimitsBatchAsync(items.Select(i => i.UserId).Distinct().ToList(), ListingKinds.Plot);
        foreach (var i in items)
            i.OwnerPlanName = limits.TryGetValue(i.UserId, out var l) ? l.PlanName : null;
    }

    public static async Task<IResult> GetPlotGoLiveRequestById(Guid id, ApplicationDbContext db, IPlanEntitlementService entitlements)
    {
        var plot = await db.PlotListings
            .AsNoTracking()
            .Include(p => p.User)
            .Include(p => p.PlotType)
            .Include(p => p.District)
            .Include(p => p.City)
            .Include(p => p.Photos.OrderBy(ph => ph.PhotoOrder))
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);
        if (plot == null) return NotFoundResponse("Go-Live request not found");
        var dto = ToGoLiveRequestDto(plot, includePhotos: true);
        dto.OwnerPlanName = (await entitlements.GetLimitsAsync(plot.UserId, ListingKinds.Plot)).PlanName;
        return OkResponse(dto);
    }

    private static NotificationEvent BuildPlotGoLiveApprovedNotification(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.GoLiveApproved,
        Title = "Your listing is now live",
        Body = "Your Plot listing has been approved and is now live.",
        ActionRoute = "/my-plots",
        CreatedAt = DateTime.UtcNow,
    };

    private static NotificationEvent BuildPlotGoLiveRejectedNotification(Guid userId, string reason) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.GoLiveRejected,
        Title = "Your listing was not approved",
        Body = $"Your Plot listing was rejected. Reason: {reason}",
        ActionRoute = "/my-plots",
        CreatedAt = DateTime.UtcNow,
    };

    // Clone of AdminHandlers.SendGoLiveNotificationAsync's shape — SignalR user_{id} push via
    // NotificationHub (the same hub every other generic NotificationEvent producer reuses) + RabbitMQ
    // notification.push publish for NotificationPushWorkerService. Best-effort: a push failure must
    // never turn an already-committed Approve/Reject into an error response.
    private static async Task SendGoLiveNotificationAsync(
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher, NotificationEvent notification)
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
            });
        }
        catch { }

        try
        {
            var payload = JsonSerializer.Serialize(new NotificationPushPayload(notification.Id, notification.TargetUserId!.Value));
            await publisher.PublishAsync("notification.push", payload);
        }
        catch { }
    }

    private static NotificationEvent BuildPlotApprovedPausedNotification(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.GoLiveApproved,
        Title = "Your listing is approved",
        Body = "Your Plot listing is approved. Your plan limit is full, so it is paused. Swap or upgrade to make it live.",
        ActionRoute = "/my-plots",
        CreatedAt = DateTime.UtcNow,
    };

    private static NotificationEvent BuildPlotApprovedSuspendedNotification(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.GoLiveApproved,
        Title = "Your listing is approved",
        Body = "Your Plot listing is approved. It goes live when your account is active again.",
        ActionRoute = "/my-plots",
        CreatedAt = DateTime.UtcNow,
    };

    // Within the owner's plan limit -> live till the resolved ValidUntil; over it -> Approved but plan-paused.
    public static async Task<IResult> ApprovePlotGoLiveRequest(
        Guid id, IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        ILiveSlotService slots, IPlanEntitlementService entitlements,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        var plot = await unitOfWork.PlotListings.GetByIdAsync(id);
        if (plot == null || plot.IsDeleted) return NotFoundResponse("PlotListing not found");
        if (plot.LiveRequestStatus != GoLiveRequestStatuses.Pending)
            return BadRequestResponse("This request has already been resolved");

        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(plot.UserId);
            var limits = await entitlements.GetLimitsAsync(plot.UserId, ListingKinds.Plot);
            var live = await slots.CountLiveAsync(plot.UserId, ListingKinds.Plot, plot.Id);
            // A suspended owner's row is approved but held as Suspended, so UnsuspendAsync revives it within the slots.
            var ownerActive = await db.Users.AsNoTracking()
                .Where(u => u.Id == plot.UserId).Select(u => u.IsActive).FirstOrDefaultAsync();
            var goesLive = ownerActive && live < limits.ListingLimit;

            plot.LiveRequestStatus = GoLiveRequestStatuses.Approved;
            plot.IsActive = goesLive;
            plot.PauseReason = goesLive ? null : ownerActive ? PauseReasons.Plan : PauseReasons.Suspended;
            plot.ValidUntil = goesLive ? entitlements.ResolveValidUntil(limits) : plot.ValidUntil;
            plot.PriorityRank = limits.PriorityRank;
            plot.IsFeatured = limits.IsFeatured;
            plot.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.PlotListings.UpdateAsync(plot);

            var notification = goesLive
                ? BuildPlotGoLiveApprovedNotification(plot.UserId)
                : ownerActive
                    ? BuildPlotApprovedPausedNotification(plot.UserId)
                    : BuildPlotApprovedSuspendedNotification(plot.UserId);
            db.NotificationEvents.Add(notification);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This plot was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }

            await unitOfWork.CommitTransactionAsync();

            if (goesLive)
            {
                var redis = sp.GetService<IConnectionMultiplexer>();
                await InvalidateNearbyCacheAsync(redis, plot.DistrictId);
                await InvalidateRecentPlotsCacheAsync(redis);
                await InvalidateForYouPlotsCacheAsync(redis, plot.DistrictId);
            }

            await SendGoLiveNotificationAsync(hubContext, publisher, notification);

            return OkResponse(new { success = true, isActive = plot.IsActive, validUntil = plot.ValidUntil, pauseReason = plot.PauseReason });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    public static async Task<IResult> RejectPlotGoLiveRequest(
        Guid id, RejectGoLiveRequest request, IValidator<RejectGoLiveRequest> validator,
        IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher,
        ILiveSlotService slots, IListingCacheInvalidator cacheInvalidator)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var plot = await unitOfWork.PlotListings.GetByIdAsync(id);
        if (plot == null || plot.IsDeleted) return NotFoundResponse("PlotListing not found");
        if (plot.LiveRequestStatus != GoLiveRequestStatuses.Pending)
            return BadRequestResponse("This request has already been resolved");

        // The Pending row held a slot; the newest plan-paused row takes it in the same transaction.
        var resumed = 0;
        NotificationEvent notification;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(plot.UserId);
            plot.LiveRequestStatus = GoLiveRequestStatuses.Rejected;
            plot.RejectedReason = request.Reason;
            plot.PauseReason = null;
            plot.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.PlotListings.UpdateAsync(plot);

            notification = BuildPlotGoLiveRejectedNotification(plot.UserId, request.Reason);
            db.NotificationEvents.Add(notification);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This plot was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }

            resumed = await slots.ResumeAfterSlotFreedAsync(plot.UserId, ListingKinds.Plot);
            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        if (resumed > 0)
            await cacheInvalidator.BustForUserAsync(plot.UserId, ListingKinds.Plot);

        await SendGoLiveNotificationAsync(hubContext, publisher, notification);

        return OkResponse(new { success = true });
    }

    // ── Report-driven takedown ("Reject") — Plot side ─────────────────────────
    // Called from AdminHandlers.RejectReport (the single PUT /admin/reports/{id}/reject endpoint,
    // dispatched on report.ListingType) — kept here, not in AdminHandlers.cs, so it can reuse this
    // file's own Plot-specific cache-key helpers (InvalidateNearbyCacheAsync/
    // InvalidateRecentPlotsCacheAsync/InvalidateForYouPlotsCacheAsync use different Redis key
    // patterns than the Room versions) instead of duplicating those key strings elsewhere.
    // Rejected rows never count against the owner's plan limit.

    private static NotificationEvent BuildPlotReportRejectedNotification(Guid userId, string reason) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.GoLiveRejected,
        Title = "Your listing was taken down",
        Body = $"Your Plot listing was taken down: {reason}",
        ActionRoute = "/my-plots",
        CreatedAt = DateTime.UtcNow,
    };

    public static async Task<IResult> RejectReportedPlotAsync(
        ListingReport report, string reason, Guid? adminId,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher, ILiveSlotService slots)
    {
        var plot = await unitOfWork.PlotListings.GetByIdAsync(report.ListingId);
        if (plot == null || plot.IsDeleted) return NotFoundResponse("PlotListing not found");

        var resumed = 0;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(plot.UserId);
            var wasLive = plot.IsActive || plot.LiveRequestStatus == GoLiveRequestStatuses.Pending;
            plot.IsActive = false;
            plot.PauseReason = null;
            plot.LiveRequestStatus = GoLiveRequestStatuses.Rejected;
            plot.RejectedReason = reason;
            // See the identical comment in AdminHandlers.RejectReportedRoomAsync — expiring ValidUntil
            // here (rather than special-casing Rejected inside GoLiveHandlers' shared free-reactivation
            // branch) is what actually prevents a silent, free, unreviewed reactivation.
            plot.ValidUntil = DateTime.UtcNow.AddDays(-1);
            plot.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.PlotListings.UpdateAsync(plot);

            var notification = BuildPlotReportRejectedNotification(plot.UserId, reason);
            db.NotificationEvents.Add(notification);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This plot was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }

            await unitOfWork.ListingReports.ResolveAsync(report.Id, adminId, "PostRejected");

            if (wasLive)
                resumed = await slots.ResumeAfterSlotFreedAsync(plot.UserId, ListingKinds.Plot);

            await unitOfWork.CommitTransactionAsync();

            var redis = sp.GetService<IConnectionMultiplexer>();
            await InvalidateNearbyCacheAsync(redis, plot.DistrictId);
            await InvalidateRecentPlotsCacheAsync(redis);
            await InvalidateForYouPlotsCacheAsync(redis, plot.DistrictId);
            if (resumed > 0)
                await sp.GetRequiredService<IListingCacheInvalidator>().BustForUserAsync(plot.UserId, ListingKinds.Plot);

            await SendGoLiveNotificationAsync(hubContext, publisher, notification);

            return OkResponse(new { success = true });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }
}
