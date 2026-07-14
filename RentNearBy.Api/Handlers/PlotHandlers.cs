using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Mapster;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
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

    private static string NearestCacheKey(Guid districtId, Guid? plotTypeId, double lat, double lng)
        => $"nearest_plot:{districtId}:{plotTypeId?.ToString() ?? "all"}:{lat:F3}:{lng:F3}";
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
                        if (!isAuth) items.ForEach(d => d.OwnerPhone = null);
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

        if (!isAuth) fetched.ForEach(d => d.OwnerPhone = null);
        return OkResponse(new { items = fetched });
    }

    // Hard server-side cap, not client-controllable — "Find Near Me" always shows the
    // closest 5, then hands off to View All for the rest.
    private const int NearMeResultLimit = 5;

    private record NearestCachePayload(List<NearMePlotListingDto> Items, int TotalMatching);

    public static async Task<IResult> GetNearest(
        double latitude, double longitude, Guid districtId, Guid? plotTypeId,
        IUnitOfWork unitOfWork,
        ClaimsPrincipal principal,
        IServiceProvider sp)
    {
        if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180)
            return BadRequestResponse("Invalid coordinates");

        var redis = sp.GetService<IConnectionMultiplexer>();
        var isAuth = principal.Identity?.IsAuthenticated == true;
        var cacheKey = NearestCacheKey(districtId, plotTypeId, latitude, longitude);

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(cacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<NearestCachePayload>(cached!);
                    if (payload != null)
                    {
                        if (!isAuth) payload.Items.ForEach(d => d.OwnerPhone = null);
                        return OkResponse(new { items = payload.Items, totalMatching = payload.TotalMatching });
                    }
                }
                catch (JsonException) { }
            }
        }

        var (items, total) = await unitOfWork.PlotListings.GetNearestAsync(
            latitude, longitude, districtId, plotTypeId, NearMeResultLimit);
        var fetched = items.ToList();

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(new NearestCachePayload(fetched, total));
            try { await redis.GetDatabase().StringSetAsync(cacheKey, json, NearbyCacheTtl, When.NotExists); } catch { }
        }

        if (!isAuth) fetched.ForEach(d => d.OwnerPhone = null);
        return OkResponse(new { items = fetched, totalMatching = total });
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
        IPaymentService paymentService,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var plotType = await unitOfWork.PlotTypes.GetByIdAsync(request.PlotTypeId);
        if (plotType == null) return BadRequestResponse("Invalid plot type");

        if (request.CityId.HasValue)
        {
            var city = await unitOfWork.Cities.GetByIdAsync(request.CityId.Value);
            if (city == null) return BadRequestResponse("Selected city does not exist");
            if (city.DistrictId != request.DistrictId)
                return BadRequestResponse("Selected city does not belong to the selected district");
        }

        var plotFeature = await unitOfWork.Features.GetByKeyAsync(FeatureKeys.PlotListingPayment);
        if (plotFeature == null || !plotFeature.IsEnabled)
        {
            var freeLimit = plotFeature?.FreeLimit ?? 1;
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var totalCount = await db.PlotListings
                .Where(p => p.UserId == userId && !p.IsDeleted)
                .CountAsync();
            if (totalCount >= freeLimit)
                return BadRequestResponse($"Free mode limit: you can have at most {freeLimit} plot(s). Delete one to add more.");
        }
        else
        {
            var membership = await unitOfWork.PlotMemberships.GetActiveByUserIdAsync(userId);
            if (membership != null && membership.IsActive)
            {
                var db = sp.GetRequiredService<ApplicationDbContext>();
                var totalCount = await db.PlotListings
                    .Where(p => p.UserId == userId && !p.IsDeleted)
                    .CountAsync();
                if (totalCount >= membership.MaxPlotListings)
                    return BadRequestResponse($"You have reached your plan limit of {membership.MaxPlotListings} plot(s). Upgrade your plan to add more.");
            }
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
            IsActive = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Auto-activate for paid plot members with available capacity
        var activeMembership = await unitOfWork.PlotMemberships.GetActiveByUserIdAsync(userId);
        if (activeMembership != null && activeMembership.IsActive)
        {
            var activePlan = await unitOfWork.PlotPlans.GetByPlanTypeAsync(activeMembership.PlanType);
            if (activePlan?.Price > 0)
            {
                var canActivate = await paymentService.CanUserActivatePlotListingAsync(userId);
                if (canActivate)
                {
                    plot.IsActive = true;
                    plot.ValidUntil = activeMembership.ValidUntil;
                }
            }
        }

        await unitOfWork.PlotListings.AddAsync(plot);
        await unitOfWork.SaveChangesAsync();

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, plot.DistrictId);

        return CreatedResponse(new { plotId = plot.Id }, $"/api/v1/plots/{plot.Id}");
    }

    public static async Task<IResult> UpdatePlotListing(
        Guid id, UpdatePlotListingRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdatePlotListingRequest> validator,
        IUnitOfWork unitOfWork,
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
        if (request.IsActive.HasValue)
        {
            if (request.IsActive.Value == true)
            {
                var plotFeature = await unitOfWork.Features.GetByKeyAsync(FeatureKeys.PlotListingPayment);
                if (plotFeature == null || !plotFeature.IsEnabled)
                {
                    var freeLimit = plotFeature?.FreeLimit ?? 1;
                    var freeDays = plotFeature?.FreeDays ?? 2;
                    var db = sp.GetRequiredService<ApplicationDbContext>();
                    var totalCount = await db.PlotListings
                        .Where(p => p.UserId == userId && !p.IsDeleted && p.Id != id)
                        .CountAsync();
                    if (totalCount >= freeLimit)
                        return BadRequestResponse($"Free mode limit: you can have at most {freeLimit} plot(s).");
                    plot.ValidUntil = DateTime.UtcNow.AddDays(freeDays);
                }
                else
                {
                    var membership = await unitOfWork.PlotMemberships.GetActiveByUserIdAsync(userId);
                    if (membership == null || !membership.IsActive)
                        return BadRequestResponse("You need an active plan to go live. Please purchase a plan.");
                    var svc = sp.GetRequiredService<IPaymentService>();
                    var canActivate = await svc.CanUserActivatePlotListingAsync(userId);
                    if (!canActivate)
                        return BadRequestResponse($"You have reached your active plot limit of {membership.MaxPlotListings}. Upgrade your plan.");
                }
            }
            plot.IsActive = request.IsActive.Value;
        }
        plot.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.PlotListings.UpdateAsync(plot);
        await unitOfWork.SaveChangesAsync();

        if (request.IsActive.HasValue && request.IsActive.Value == false)
            await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(id, "Plot");

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, plot.DistrictId);
        if (oldDistrictId != plot.DistrictId)
            await InvalidateNearbyCacheAsync(redis, oldDistrictId);

        return OkResponse(new { success = true });
    }

    public static async Task<IResult> DeletePlotListing(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork,
        IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var plot = await unitOfWork.PlotListings.GetByIdWithPhotosAsync(id);
        if (plot == null) return NotFoundResponse("PlotListing not found");
        if (plot.UserId != userId) return ForbiddenResponse("You do not own this plot");

        var districtId = plot.DistrictId;

        foreach (var photo in plot.Photos)
            await photoService.DeletePhotoAsync(photo.FilePath);

        plot.IsDeleted = true;
        plot.DeletedAt = DateTime.UtcNow;
        await unitOfWork.PlotListings.UpdateAsync(plot);
        await unitOfWork.SaveChangesAsync();

        await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(id, "Plot");

        await InvalidateNearbyCacheAsync(sp.GetService<IConnectionMultiplexer>(), districtId);

        return NoContentResponse();
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
        bool IsActive, string? DistrictName, string? CityName, string? Address,
        string? ThumbnailUrl, int PhotoCount, DateTime CreatedAt);

    public static async Task<IResult> GetAdminPlotListings(
        IUnitOfWork unitOfWork,
        int page = 1, int pageSize = 20,
        string? plotType = null, bool? isActive = null, Guid? districtId = null, Guid? cityId = null)
    {
        if (pageSize < 1 || pageSize > 100) pageSize = 20;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.PlotListings.GetAllAsync(page, pageSize, plotType, isActive, districtId, cityId);
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
            DistrictName: p.District?.Name,
            CityName: p.City?.Name,
            Address: p.Address,
            ThumbnailUrl: p.Photos.FirstOrDefault()?.PhotoUrl,
            PhotoCount: p.Photos.Count,
            CreatedAt: p.CreatedAt
        )).ToList();

        return OkResponse(new { items = dtos, hasMore });
    }

    public static async Task<IResult> GetAdminPlotById(Guid id, IUnitOfWork unitOfWork)
    {
        var plot = await unitOfWork.PlotListings.GetByIdWithPhotosForAdminAsync(id);
        if (plot == null) return NotFoundResponse("PlotListing not found");
        return OkResponse(plot.Adapt<PlotListingDto>());
    }

    public static async Task<IResult> AdminTogglePlotListing(
        Guid id, AdminTogglePlotListingRequest request,
        IUnitOfWork unitOfWork, IServiceProvider sp)
    {
        var plot = await unitOfWork.PlotListings.GetByIdAsync(id);
        if (plot == null) return NotFoundResponse("PlotListing not found");

        if (request.IsActive)
        {
            var plotFeature = await unitOfWork.Features.GetByKeyAsync(FeatureKeys.PlotListingPayment);
            if (plotFeature != null && plotFeature.IsEnabled)
            {
                var membership = await unitOfWork.PlotMemberships.GetActiveByUserIdAsync(plot.UserId);
                if (membership == null || !membership.IsActive)
                    return BadRequestResponse("This user does not have an active membership.");
                var svc = sp.GetRequiredService<IPaymentService>();
                var canActivate = await svc.CanUserActivatePlotListingAsync(plot.UserId);
                if (!canActivate)
                    return BadRequestResponse($"This user has reached their active plot limit of {membership.MaxPlotListings}.");
            }
        }

        plot.IsActive = request.IsActive;
        plot.UpdatedAt = DateTime.UtcNow;
        await unitOfWork.PlotListings.UpdateAsync(plot);
        await unitOfWork.SaveChangesAsync();

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, plot.DistrictId);

        return OkResponse(new { success = true, isActive = plot.IsActive });
    }

    public static async Task<IResult> AdminDeletePlotListing(
        Guid id, IUnitOfWork unitOfWork,
        IPhotoService photoService, IServiceProvider sp)
    {
        var plot = await unitOfWork.PlotListings.GetByIdWithPhotosAsync(id);
        if (plot == null) return NotFoundResponse("PlotListing not found");

        var districtId = plot.DistrictId;

        foreach (var photo in plot.Photos)
            await photoService.DeletePhotoAsync(photo.FilePath);

        plot.IsDeleted = true;
        plot.DeletedAt = DateTime.UtcNow;
        await unitOfWork.PlotListings.UpdateAsync(plot);
        await unitOfWork.SaveChangesAsync();

        await InvalidateNearbyCacheAsync(sp.GetService<IConnectionMultiplexer>(), districtId);

        return NoContentResponse();
    }

    // ── PlotListing Payment handlers ────────────────────────────────────────────────

    public static async Task<IResult> CreatePlotListingOrder(
        Guid plotId, string planType,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.CreatePlotListingOrderAsync(userId, plotId, planType.ToUpperInvariant());
            return OkResponse(response);
        }
        catch (KeyNotFoundException ex) { return NotFoundResponse(ex.Message); }
        catch (UnauthorizedAccessException ex) { return UnauthorizedResponse(ex.Message); }
        catch (ArgumentException ex) { return BadRequestResponse(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequestResponse(ex.Message); }
        catch (Exception) { return ServerErrorResponse(); }
    }

    public static async Task<IResult> VerifyPlotListingPayment(
        Guid plotId, VerifyPaymentRequest request,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.VerifyPlotListingPaymentAsync(userId, request);
            return OkResponse(response);
        }
        catch (KeyNotFoundException ex) { return NotFoundResponse(ex.Message); }
        catch (UnauthorizedAccessException ex) { return UnauthorizedResponse(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequestResponse(ex.Message); }
        catch (Exception) { return ServerErrorResponse(); }
    }

    public static async Task<IResult> CreatePlotListingUpgradeOrder(
        string planType,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.CreatePlotListingUpgradeOrderAsync(userId, planType.ToUpperInvariant());
            return OkResponse(response);
        }
        catch (ArgumentException ex) { return BadRequestResponse(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequestResponse(ex.Message); }
        catch (Exception) { return ServerErrorResponse(); }
    }

    public static async Task<IResult> VerifyPlotListingUpgradePayment(
        VerifyPaymentRequest request,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.VerifyPlotListingUpgradePaymentAsync(userId, request);
            return OkResponse(response);
        }
        catch (KeyNotFoundException ex) { return NotFoundResponse(ex.Message); }
        catch (UnauthorizedAccessException ex) { return UnauthorizedResponse(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequestResponse(ex.Message); }
        catch (Exception) { return ServerErrorResponse(); }
    }

    public static async Task<IResult> GetPublicPlotPlans(IUnitOfWork unitOfWork)
    {
        var plans = await unitOfWork.PlotPlans.GetAllAsync();
        var result = plans.Where(p => p.IsEnabled).OrderBy(p => p.Price)
            .Select(p => new { planType = p.PlanType, days = p.Days, price = p.Price, originalPrice = p.OriginalPrice, discountPercent = p.DiscountPercent, plotLimit = p.PlotListingLimit })
            .ToList();
        return OkResponse(result);
    }

    public static async Task<IResult> GetPlotMembershipStatus(
        ClaimsPrincipal principal,
        IPaymentService paymentService,
        IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var membership = await unitOfWork.PlotMemberships.GetActiveByUserIdAsync(userId);
            var activePlotListings = await paymentService.GetActivePlotListingCountAsync(userId);
            var canActivate = await paymentService.CanUserActivatePlotListingAsync(userId);

            return OkResponse(new
            {
                hasMembership = membership != null,
                planType = membership?.PlanType,
                validUntil = membership?.ValidUntil,
                maxPlotListings = membership?.MaxPlotListings ?? 0,
                activePlotListings,
                canActivate
            });
        }
        catch (Exception)
        {
            return ServerErrorResponse();
        }
    }
}
