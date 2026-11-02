using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class TripHandlers
{
    private const int MaxSpotsPageSize = 30;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 30;
    private const int MaxPage = 1000;
    private const int PlanMaxPerWindow = 30;
    private static readonly TimeSpan PlanWindow = TimeSpan.FromMinutes(1);
    private const int CitiesMaxPerWindow = 60;
    private static readonly TimeSpan CitiesWindow = TimeSpan.FromMinutes(1);
    private const int MaxCitiesPageSize = 200;
    private const int MaxCitySearchLength = 100;
    private const double MaxSpotDistanceKm = 200;

    private static IResult TripNotFound() => ErrorResponse(404, "Trip not found", TripErrorTypes.TripNotFound);

    private static IResult CityNotFound() => ErrorResponse(404, "City not found", TripErrorTypes.CityNotFound);

    private static IResult CitiesUnavailable()
        => ErrorResponse(503, "Cities are not available right now", TripErrorTypes.CitiesUnavailable);

    private static IResult Invalid(ValidationResult validation)
    {
        var error = validation.Errors[0];
        return BadRequestResponse(error.ErrorMessage,
            error.ErrorCode == TripErrorTypes.TooManySpots ? TripErrorTypes.TooManySpots : "BadRequest");
    }

    public static async Task<IResult> GetCities(
        ClaimsPrincipal principal, ITripCityCatalog catalog, IRateLimitService rateLimiter, IOptions<TripPlannerOptions> options,
        HttpContext httpContext, string? q = null, int page = 1, int pageSize = 0)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await CitiesRateLimitedAsync(userId, rateLimiter, httpContext) is { } limited) return limited;

        var pageNo = Math.Clamp(page, 1, MaxPage);
        var size = Math.Clamp(pageSize <= 0 ? options.Value.CitiesPageSize : pageSize, 1, MaxCitiesPageSize);
        var text = q?.Trim();
        if (text != null && text.Length > MaxCitySearchLength) return BadRequestResponse("Search text is too long", "BadRequest");

        var snapshot = await catalog.GetAsync();
        if (snapshot == null) return CitiesUnavailable();

        var etag = $"W/\"{snapshot.Version}:{pageNo}:{size}\"";
        if (string.IsNullOrEmpty(text)) httpContext.Response.Headers.ETag = etag;
        if (string.IsNullOrEmpty(text) && httpContext.Request.Headers.IfNoneMatch.ToString() == etag)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        var matches = TripCityCatalog.FilterCities(snapshot.Cities, text);
        var skip = (pageNo - 1) * size;
        return OkResponse(new TripCityPageDto
        {
            Items = matches.Skip(skip).Take(size).Select(ToCityDto).ToList(),
            HasMore = skip + size < matches.Count,
            Page = pageNo,
            PageSize = size,
            Version = snapshot.Version,
            Count = snapshot.Count,
            Stale = snapshot.IsStale,
        });
    }

    public static async Task<IResult> GetCitiesVersion(
        ClaimsPrincipal principal, ITripCityCatalog catalog, IRateLimitService rateLimiter, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await CitiesRateLimitedAsync(userId, rateLimiter, httpContext) is { } limited) return limited;

        var snapshot = await catalog.GetAsync();
        if (snapshot == null) return CitiesUnavailable();

        var etag = $"W/\"{snapshot.Version}\"";
        httpContext.Response.Headers.ETag = etag;
        if (httpContext.Request.Headers.IfNoneMatch.ToString() == etag)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        return OkResponse(new TripCityVersionDto
        {
            Version = snapshot.Version,
            Count = snapshot.Count,
            UpdatedAt = snapshot.UpdatedAt,
            Stale = snapshot.IsStale,
        });
    }

    private static async Task<IResult?> CitiesRateLimitedAsync(Guid userId, IRateLimitService rateLimiter, HttpContext httpContext)
    {
        var rl = await rateLimiter.CheckAsync($"trip:cities:{userId}", CitiesMaxPerWindow, CitiesWindow);
        if (rl.IsAllowed) return null;
        httpContext.Response.Headers["Retry-After"] = ((int)(rl.RetryAfter ?? CitiesWindow).TotalSeconds).ToString();
        return TooManyRequestsResponse();
    }

    public static async Task<IResult> GetCitySpots(
        long cityKey, ITripCityCatalog catalog, ITripSpotCache cache, IOptions<TripPlannerOptions> options,
        string? cursor = null, int pageSize = 0)
    {
        var size = Math.Clamp(pageSize <= 0 ? options.Value.SpotsPageSize : pageSize, 1, MaxSpotsPageSize);
        if (cityKey <= 0) return CityNotFound();

        var (available, city) = await catalog.FindAsync(cityKey);
        if (available && city == null) return CityNotFound();

        var cached = await cache.GetAsync(cityKey);
        if (cached == null)
            return ErrorResponse(503, "Places are not available right now", TripErrorTypes.SpotsUnavailable);

        var cityName = string.IsNullOrEmpty(cached.CityName) ? city?.Name ?? string.Empty : cached.CityName;
        if (cached.Status == TripSpotStatuses.NotReady)
            return OkResponse(EmptySpotsPage(cityName, cityKey));

        var offset = int.TryParse(cursor, out var value) ? Math.Max(0, value) : 0;
        var total = cached.Spots.Count;
        var hasMore = offset + size < total;
        return OkResponse(new TripSpotsPageDto
        {
            Items = cached.Spots.Skip(offset).Take(size).ToList(),
            HasMore = hasMore,
            NextCursor = hasMore ? (offset + size).ToString() : null,
            Status = cached.Status,
            CityName = cityName,
            CityKey = cityKey,
            Total = total,
            GeneratedAt = cached.GeneratedAt,
        });
    }

    public static async Task<IResult> Plan(
        PlanTripRequest request, IValidator<PlanTripRequest> validator, ClaimsPrincipal principal, IUnitOfWork unitOfWork,
        ITripCityCatalog catalog, ITripPlannerService planner, IRateLimitService rateLimiter, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var rl = await rateLimiter.CheckAsync($"trip:plan:{userId}", PlanMaxPerWindow, PlanWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)(rl.RetryAfter ?? PlanWindow).TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var (city, error) = await ResolvePlanCityAsync(request.CityKey, userId, catalog, unitOfWork);
        if (city == null) return error!;

        if (!SpotsNearCity(city, request))
            return BadRequestResponse(PlanTripRequestValidator.InvalidCoordinatesMessage, "BadRequest");

        return OkResponse(await planner.PlanAsync(city, request));
    }

    // Live catalog first; a city that is no longer live still plans from the caller's own saved snapshot.
    private static async Task<(TripCityInfo? City, IResult? Error)> ResolvePlanCityAsync(
        long cityKey, Guid userId, ITripCityCatalog catalog, IUnitOfWork unitOfWork)
    {
        var (available, city) = await catalog.FindAsync(cityKey);
        if (city != null) return (city, null);
        var saved = await unitOfWork.Trips.FindCitySnapshotAsync(userId, cityKey);
        if (saved != null) return (saved, null);
        return (null, available ? CityNotFound() : CitiesUnavailable());
    }

    // Places must lie around the trip city, so the planner never routes arbitrary points.
    private static bool SpotsNearCity(TripCityInfo city, PlanTripRequest request)
    {
        var lat = city.Latitude;
        var lon = city.Longitude;
        var spots = request.Mode == TripPlanModes.Recalculate
            ? (request.Days ?? new List<PlanDayInput>()).SelectMany(d => d.Spots)
            : request.Spots ?? new List<PlanSpotInput>();
        return spots.All(s => GeoMath.HaversineKm(lat, lon, s.Latitude, s.Longitude) <= MaxSpotDistanceKm);
    }

    public static async Task<IResult> GetMine(ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = DefaultPageSize)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var pageNo = Math.Clamp(page, 1, MaxPage);
        var size = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var (items, hasMore) = await unitOfWork.Trips.GetByOwnerPagedAsync(userId, pageNo, size);

        return OkResponse(new TripListDto
        {
            Items = items.Select(ToSummary).ToList(),
            HasMore = hasMore,
        });
    }

    public static async Task<IResult> Create(
        SaveTripRequest request, IValidator<SaveTripRequest> validator, ClaimsPrincipal principal, IUnitOfWork unitOfWork,
        ITripCityCatalog catalog)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        // A new trip needs a live city; the server snapshot wins over the client copy.
        var (available, city) = await catalog.FindAsync(request.Plan.CityKey);
        if (!available) return CitiesUnavailable();
        if (city == null) return CityNotFound();

        var districtId = await unitOfWork.Districts.FindActiveContainingAsync(city.Latitude, city.Longitude);

        var plan = request.Plan;
        plan.CityKey = city.CityKey;
        plan.CityName = city.Name;
        plan.DistrictId = districtId;

        var now = DateTime.UtcNow;
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            CityKey = city.CityKey,
            CityName = city.Name,
            CityLatitude = city.Latitude,
            CityLongitude = city.Longitude,
            DistrictId = districtId,
            Title = TitleOrDefault(request.Title, city.Name),
            Status = request.Status ?? TripStatuses.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ApplyPlan(trip, plan);

        await unitOfWork.Trips.AddAsync(trip);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(ToDto(trip, plan), $"/api/v1/trips/{trip.Id}");
    }

    public static async Task<IResult> GetById(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(id, userId);
        if (trip == null) return TripNotFound();

        return OkResponse(ToDto(trip, ReadPlan(trip)));
    }

    public static async Task<IResult> Update(
        Guid id, SaveTripRequest request, IValidator<SaveTripRequest> validator, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(id, userId);
        if (trip == null) return TripNotFound();

        var status = request.Status ?? trip.Status;
        if (status == TripStatuses.Planned && request.Plan.Days.Sum(d => d.Spots.Count) == 0)
            return BadRequestResponse(PlanTripRequestValidator.NoPlacesMessage);

        // The trip's city never changes; the row's snapshot wins over the client's copy.
        // A district activated after create is picked up here; a found district is never re-resolved.
        trip.DistrictId ??= await unitOfWork.Districts.FindActiveContainingAsync(trip.CityLatitude, trip.CityLongitude);
        var plan = request.Plan;
        plan.CityKey = trip.CityKey;
        plan.CityName = trip.CityName;
        plan.DistrictId = trip.DistrictId;

        if (request.Title != null) trip.Title = TitleOrDefault(request.Title, trip.CityName);
        if (request.Status != null) trip.Status = request.Status;
        ApplyPlan(trip, plan);
        trip.UpdatedAt = DateTime.UtcNow;

        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConflictResponse("This trip was just modified. Please retry.", "CONCURRENT_UPDATE");
        }

        return OkResponse(ToDto(trip, plan));
    }

    public static async Task<IResult> Delete(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(id, userId);
        if (trip == null) return TripNotFound();

        await unitOfWork.Trips.DeleteAsync(trip);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConflictResponse("This trip was just modified. Please retry.", "CONCURRENT_UPDATE");
        }

        return OkResponse(new { message = "Trip deleted" });
    }

    private static TripSpotsPageDto EmptySpotsPage(string cityName, long cityKey) => new()
    {
        Items = new List<TripSpotDto>(),
        HasMore = false,
        NextCursor = null,
        Status = TripSpotStatuses.NotReady,
        CityName = cityName,
        CityKey = cityKey,
        Total = 0,
    };

    private static string TitleOrDefault(string? title, string cityName)
    {
        var trimmed = title?.Trim();
        if (!string.IsNullOrEmpty(trimmed)) return trimmed;
        var fallback = $"{cityName} trip";
        return fallback.Length <= 80 ? fallback : fallback[..80];
    }

    private static void ApplyPlan(Trip trip, TripPlan plan)
    {
        trip.PlanJson = JsonSerializer.Serialize(plan, TripJson.Options);
        trip.DayCount = plan.Days.Count;
        trip.SpotCount = plan.Days.Sum(d => d.Spots.Count);
    }

    private static TripPlan ReadPlan(Trip trip)
    {
        try
        {
            return JsonSerializer.Deserialize<TripPlan>(trip.PlanJson, TripJson.Options) ?? new TripPlan();
        }
        catch (JsonException)
        {
            return new TripPlan();
        }
    }

    private static TripDto ToDto(Trip trip, TripPlan plan) => new()
    {
        Id = trip.Id,
        Title = trip.Title,
        Status = trip.Status,
        CityKey = trip.CityKey,
        CityName = trip.CityName,
        CityLatitude = trip.CityLatitude,
        CityLongitude = trip.CityLongitude,
        DistrictId = trip.DistrictId,
        DayCount = trip.DayCount,
        SpotCount = trip.SpotCount,
        Plan = plan,
        CreatedAt = trip.CreatedAt,
        UpdatedAt = trip.UpdatedAt,
    };

    private static TripCityDto ToCityDto(TripCityInfo c) => new()
    {
        CityKey = c.CityKey,
        Name = c.Name,
        DistrictName = c.DistrictName,
        StateName = c.StateName,
        Latitude = c.Latitude,
        Longitude = c.Longitude,
        ImageUrl = c.ImageUrl,
        Status = c.Status,
        SpotCount = c.SpotCount,
    };

    private static TripSummaryDto ToSummary(Trip trip)
    {
        var plan = ReadPlan(trip);
        return new TripSummaryDto
        {
            Id = trip.Id,
            Title = trip.Title,
            Status = trip.Status,
            CityName = trip.CityName,
            DayCount = trip.DayCount,
            SpotCount = trip.SpotCount,
            TaxiDays = plan.Days.Count(d => d.Taxi != null),
            StayDays = plan.Days.Count(d => d.Stay != null),
            StayName = plan.Days.Select(d => d.Stay?.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
            Category = plan.Days.SelectMany(d => d.Spots).Select(s => s.Category).FirstOrDefault() ?? "attraction",
            UpdatedAt = trip.UpdatedAt,
        };
    }
}
