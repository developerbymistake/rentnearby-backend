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

public static class PlotHandlers
{
    private static readonly string[] AllowedAreaUnits = ["sqft", "sqm", "bigha", "marla", "acre", "kanal"];

    private static decimal ToSqft(decimal value, string unit) => unit switch
    {
        "sqft"  => value,
        "sqm"   => value * 10.764m,
        "marla" => value * 272.25m,
        "bigha" => value * 27000m,
        "acre"  => value * 43560m,
        "kanal" => value * 5445m,
        _       => value
    };

    private static readonly TimeSpan ContextCacheTtl = TimeSpan.FromMinutes(10);
    private static string ContextCacheKey(double lat, double lng) => $"plot_context:{lat:F2}:{lng:F2}";

    private static readonly TimeSpan NearbyCacheTtl = TimeSpan.FromSeconds(60);
    private static string NearbyCacheKey(Guid cityId, double radius, double lat, double lng)
        => $"nearby_plot:{cityId}:{radius:F1}:{lat:F3}:{lng:F3}";
    private static string NearbyCityPattern(Guid cityId) => $"nearby_plot:{cityId}:*";

    private static async Task InvalidateNearbyCacheAsync(IConnectionMultiplexer? redis, Guid cityId)
    {
        if (redis == null) return;
        try
        {
            var db = redis.GetDatabase();
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
            if (server == null) return;
            await foreach (var key in server.KeysAsync(pattern: NearbyCityPattern(cityId)))
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

    public static async Task<IResult> GetContext(double lat, double lng, IUnitOfWork unitOfWork, IServiceProvider sp)
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

        var districts = (await unitOfWork.Districts.GetAllWithCitiesAsync()).ToList();
        if (districts.Count == 0) return BadRequestResponse("No districts configured");

        var nearestDistrict = districts
            .Select(d => new { District = d, Dist = Haversine(lat, lng, (double)(d.Latitude ?? 0), (double)(d.Longitude ?? 0)) })
            .MinBy(x => x.Dist)!.District;

        var cities = nearestDistrict.Cities.ToList();
        var nearestCity = cities
            .Where(c => c.Latitude.HasValue && c.Longitude.HasValue)
            .Select(c => new { City = c, Dist = Haversine(lat, lng, (double)c.Latitude!, (double)c.Longitude!) })
            .MinBy(x => x.Dist)?.City ?? cities.FirstOrDefault();

        var result = new
        {
            district = new { id = nearestDistrict.Id, name = nearestDistrict.Name, latitude = nearestDistrict.Latitude, longitude = nearestDistrict.Longitude },
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
        double latitude, double longitude, double radius, Guid cityId,
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
        var cacheKey = NearbyCacheKey(cityId, radius, latitude, longitude);

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(cacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var items = JsonSerializer.Deserialize<List<NearbyPlotDto>>(cached!);
                    if (items != null)
                    {
                        if (!isAuth) items.ForEach(d => d.OwnerPhone = null);
                        return OkResponse(new { items });
                    }
                }
                catch (JsonException) { }
            }
        }

        var fetched = (await unitOfWork.Plots.GetNearbyAsync(latitude, longitude, radius, cityId)).ToList();

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(fetched);
            try { await redis.GetDatabase().StringSetAsync(cacheKey, json, NearbyCacheTtl, When.NotExists); } catch { }
        }

        if (!isAuth) fetched.ForEach(d => d.OwnerPhone = null);
        return OkResponse(new { items = fetched });
    }

    public static async Task<IResult> GetById(Guid id, IUnitOfWork unitOfWork, ClaimsPrincipal principal)
    {
        var plot = await unitOfWork.Plots.GetByIdWithPhotosAsync(id);
        if (plot == null) return NotFoundResponse("Plot not found");
        var dto = plot.Adapt<PlotDto>();
        if (principal.Identity?.IsAuthenticated != true) dto.OwnerPhone = null;
        return OkResponse(dto);
    }

    public static async Task<IResult> GetMyPlots(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 10)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();
        if (pageSize < 1 || pageSize > 50) pageSize = 10;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.Plots.GetByUserIdPagedAsync(userId, page, pageSize);
        return OkResponse(new { items = items.Select(p => p.Adapt<PlotDto>()).ToList(), hasMore });
    }

    public static async Task<IResult> CreatePlot(
        CreatePlotRequest request,
        ClaimsPrincipal principal,
        IValidator<CreatePlotRequest> validator,
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

        var plotFeature = await unitOfWork.Features.GetByKeyAsync(FeatureKeys.PlotPayment);
        if (plotFeature == null || !plotFeature.IsEnabled)
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var activeCount = await db.Plots
                .Where(p => p.UserId == userId && p.IsActive && !p.IsDeleted)
                .CountAsync();
            if (activeCount >= 2)
                return BadRequestResponse("Free mode limit: you can have at most 2 active plots. Deactivate one to add more.");
        }
        else
        {
            var existingMembership = await unitOfWork.PlotMemberships.GetActiveByUserIdAsync(userId);
            if (existingMembership != null)
            {
                var canActivate = await paymentService.CanUserActivatePlotAsync(userId);
                if (!canActivate)
                    return BadRequestResponse($"You have reached your plot plan limit of {existingMembership.MaxPlots} active plot(s). Please upgrade your plan.");
            }
        }

        var plot = new Plot
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
                var canActivate = await paymentService.CanUserActivatePlotAsync(userId);
                if (canActivate)
                {
                    plot.IsActive = true;
                    plot.ValidUntil = activeMembership.ValidUntil;
                }
            }
        }

        await unitOfWork.Plots.AddAsync(plot);
        await unitOfWork.SaveChangesAsync();

        var redis = sp.GetService<IConnectionMultiplexer>();
        if (request.CityId.HasValue)
            await InvalidateNearbyCacheAsync(redis, request.CityId.Value);

        return CreatedResponse(new { plotId = plot.Id }, $"/api/v1/plots/{plot.Id}");
    }

    public static async Task<IResult> UpdatePlot(
        Guid id, UpdatePlotRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdatePlotRequest> validator,
        IUnitOfWork unitOfWork,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var plot = await unitOfWork.Plots.GetByIdAsync(id);
        if (plot == null) return NotFoundResponse("Plot not found");
        if (plot.UserId != userId) return ForbiddenResponse("You do not own this plot");

        var oldCityId = plot.CityId;

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
                var plotFeature = await unitOfWork.Features.GetByKeyAsync(FeatureKeys.PlotPayment);
                if (plotFeature == null || !plotFeature.IsEnabled)
                {
                    var db = sp.GetRequiredService<ApplicationDbContext>();
                    var activeCount = await db.Plots
                        .Where(p => p.UserId == userId && p.IsActive && !p.IsDeleted && p.Id != id)
                        .CountAsync();
                    if (activeCount >= 2)
                        return BadRequestResponse("Free mode limit: you can have at most 2 active plots.");
                    plot.ValidUntil = DateTime.UtcNow.AddDays(2);
                }
            }
            plot.IsActive = request.IsActive.Value;
        }
        plot.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.Plots.UpdateAsync(plot);
        await unitOfWork.SaveChangesAsync();

        var redis = sp.GetService<IConnectionMultiplexer>();
        if (plot.CityId.HasValue) await InvalidateNearbyCacheAsync(redis, plot.CityId.Value);
        if (oldCityId.HasValue && oldCityId != plot.CityId)
            await InvalidateNearbyCacheAsync(redis, oldCityId.Value);

        return OkResponse(new { success = true });
    }

    public static async Task<IResult> DeletePlot(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork,
        IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var plot = await unitOfWork.Plots.GetByIdWithPhotosAsync(id);
        if (plot == null) return NotFoundResponse("Plot not found");
        if (plot.UserId != userId) return ForbiddenResponse("You do not own this plot");

        var cityId = plot.CityId;

        foreach (var photo in plot.Photos)
            await photoService.DeletePhotoAsync(photo.FilePath);

        plot.IsDeleted = true;
        plot.DeletedAt = DateTime.UtcNow;
        await unitOfWork.Plots.UpdateAsync(plot);
        await unitOfWork.SaveChangesAsync();

        if (cityId.HasValue)
            await InvalidateNearbyCacheAsync(sp.GetService<IConnectionMultiplexer>(), cityId.Value);

        return NoContentResponse();
    }

    public static async Task<IResult> UploadPhoto(
        Guid id, IFormFile photo,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork,
        IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var plot = await unitOfWork.Plots.GetByIdWithPhotosAsync(id);
        if (plot == null) return NotFoundResponse("Plot not found");
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

        await unitOfWork.Plots.AddPhotoAsync(plotPhoto);
        await unitOfWork.SaveChangesAsync();

        var redis = sp.GetService<IConnectionMultiplexer>();
        if (plot.CityId.HasValue)
            await InvalidateNearbyCacheAsync(redis, plot.CityId.Value);

        return CreatedResponse(new { photoUrl = url, photoId = plotPhoto.Id }, url);
    }

    public static async Task<IResult> DeletePhoto(
        Guid id, Guid photoId,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork,
        IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var plot = await unitOfWork.Plots.GetByIdWithPhotosAsync(id);
        if (plot == null) return NotFoundResponse("Plot not found");
        if (plot.UserId != userId) return ForbiddenResponse("You do not own this plot");

        var photo = plot.Photos.FirstOrDefault(p => p.Id == photoId);
        if (photo == null) return NotFoundResponse("Photo not found");

        await photoService.DeletePhotoAsync(photo.FilePath);
        unitOfWork.Plots.RemovePhoto(photo);
        await unitOfWork.SaveChangesAsync();

        var redis = sp.GetService<IConnectionMultiplexer>();
        if (plot.CityId.HasValue)
            await InvalidateNearbyCacheAsync(redis, plot.CityId.Value);

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

    // ── Admin Plot endpoints ───────────────────────────────────────────────────

    public record AdminTogglePlotRequest(bool IsActive);

    public record AdminPlotDto(
        string Id, string UserId, string? OwnerName, string? OwnerPhone,
        string PlotType, double AreaValue, string AreaUnit, double AreaSqft,
        bool IsActive, string? DistrictName, string? CityName, string? Address,
        string? ThumbnailUrl, int PhotoCount, DateTime CreatedAt);

    public static async Task<IResult> GetAdminPlots(
        IUnitOfWork unitOfWork,
        int page = 1, int pageSize = 20,
        string? plotType = null, bool? isActive = null, Guid? districtId = null, Guid? cityId = null)
    {
        if (pageSize < 1 || pageSize > 100) pageSize = 20;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.Plots.GetAllAsync(page, pageSize, plotType, isActive, districtId, cityId);
        var dtos = items.Select(p => new AdminPlotDto(
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

    public static async Task<IResult> AdminTogglePlot(
        Guid id, AdminTogglePlotRequest request,
        IUnitOfWork unitOfWork, IServiceProvider sp)
    {
        var plot = await unitOfWork.Plots.GetByIdAsync(id);
        if (plot == null) return NotFoundResponse("Plot not found");

        plot.IsActive = request.IsActive;
        plot.UpdatedAt = DateTime.UtcNow;
        await unitOfWork.Plots.UpdateAsync(plot);
        await unitOfWork.SaveChangesAsync();

        var redis = sp.GetService<IConnectionMultiplexer>();
        if (plot.CityId.HasValue)
            await InvalidateNearbyCacheAsync(redis, plot.CityId.Value);

        return OkResponse(new { success = true, isActive = plot.IsActive });
    }

    public static async Task<IResult> AdminDeletePlot(
        Guid id, IUnitOfWork unitOfWork,
        IPhotoService photoService, IServiceProvider sp)
    {
        var plot = await unitOfWork.Plots.GetByIdWithPhotosAsync(id);
        if (plot == null) return NotFoundResponse("Plot not found");

        var cityId = plot.CityId;

        foreach (var photo in plot.Photos)
            await photoService.DeletePhotoAsync(photo.FilePath);

        plot.IsDeleted = true;
        plot.DeletedAt = DateTime.UtcNow;
        await unitOfWork.Plots.UpdateAsync(plot);
        await unitOfWork.SaveChangesAsync();

        if (cityId.HasValue)
            await InvalidateNearbyCacheAsync(sp.GetService<IConnectionMultiplexer>(), cityId.Value);

        return NoContentResponse();
    }

    // ── Plot Payment handlers ────────────────────────────────────────────────

    public static async Task<IResult> CreatePlotOrder(
        Guid plotId, string planType,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.CreatePlotOrderAsync(userId, plotId, planType.ToUpperInvariant());
            return OkResponse(response);
        }
        catch (KeyNotFoundException ex) { return NotFoundResponse(ex.Message); }
        catch (UnauthorizedAccessException ex) { return UnauthorizedResponse(ex.Message); }
        catch (ArgumentException ex) { return BadRequestResponse(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequestResponse(ex.Message); }
        catch (Exception) { return ServerErrorResponse(); }
    }

    public static async Task<IResult> VerifyPlotPayment(
        Guid plotId, VerifyPaymentRequest request,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.VerifyPlotPaymentAsync(userId, request);
            return OkResponse(response);
        }
        catch (KeyNotFoundException ex) { return NotFoundResponse(ex.Message); }
        catch (UnauthorizedAccessException ex) { return UnauthorizedResponse(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequestResponse(ex.Message); }
        catch (Exception) { return ServerErrorResponse(); }
    }

    public static async Task<IResult> CreatePlotUpgradeOrder(
        string planType,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.CreatePlotUpgradeOrderAsync(userId, planType.ToUpperInvariant());
            return OkResponse(response);
        }
        catch (ArgumentException ex) { return BadRequestResponse(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequestResponse(ex.Message); }
        catch (Exception) { return ServerErrorResponse(); }
    }

    public static async Task<IResult> VerifyPlotUpgradePayment(
        VerifyPaymentRequest request,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.VerifyPlotUpgradePaymentAsync(userId, request);
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
            .Select(p => new { planType = p.PlanType, days = p.Days, price = p.Price, plotLimit = p.PlotLimit })
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
            var activePlots = await paymentService.GetActivePlotCountAsync(userId);
            var canActivate = await paymentService.CanUserActivatePlotAsync(userId);

            return OkResponse(new
            {
                hasMembership = membership != null,
                planType = membership?.PlanType,
                validUntil = membership?.ValidUntil,
                maxPlots = membership?.MaxPlots ?? 0,
                activePlots,
                canActivate
            });
        }
        catch (Exception)
        {
            return ServerErrorResponse();
        }
    }
}
