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
    private const double MaxSpotDistanceKm = 200;

    private static IResult TripNotFound() => ErrorResponse(404, "Trip not found", TripErrorTypes.TripNotFound);

    private static IResult CityNotFound() => ErrorResponse(404, "City not found", TripErrorTypes.CityNotFound);

    private static IResult Invalid(ValidationResult validation)
    {
        var error = validation.Errors[0];
        return BadRequestResponse(error.ErrorMessage,
            error.ErrorCode == TripErrorTypes.TooManySpots ? TripErrorTypes.TooManySpots : "BadRequest");
    }

    public static async Task<IResult> GetCitySpots(
        Guid cityId, IUnitOfWork unitOfWork, ITripSpotCache cache, IOptions<TripPlannerOptions> options,
        string? cursor = null, int pageSize = 0)
    {
        var size = Math.Clamp(pageSize <= 0 ? options.Value.SpotsPageSize : pageSize, 1, MaxSpotsPageSize);

        var city = await unitOfWork.Cities.GetByIdAsync(cityId);
        if (city == null) return CityNotFound();

        if (city.CityKey == null)
            return OkResponse(EmptySpotsPage(city.Name, null));

        var cached = await cache.GetAsync(city.CityKey.Value);
        if (cached == null)
            return ErrorResponse(503, "Places are not available right now", TripErrorTypes.SpotsUnavailable);
        if (cached.Status == TripSpotStatuses.NotReady)
            return OkResponse(EmptySpotsPage(city.Name, city.CityKey));

        var offset = int.TryParse(cursor, out var value) ? Math.Max(0, value) : 0;
        var total = cached.Spots.Count;
        var hasMore = offset + size < total;
        return OkResponse(new TripSpotsPageDto
        {
            Items = cached.Spots.Skip(offset).Take(size).ToList(),
            HasMore = hasMore,
            NextCursor = hasMore ? (offset + size).ToString() : null,
            Status = cached.Status,
            CityName = city.Name,
            CityKey = city.CityKey,
            Total = total,
            GeneratedAt = cached.GeneratedAt,
        });
    }

    public static async Task<IResult> Plan(
        PlanTripRequest request, IValidator<PlanTripRequest> validator, ClaimsPrincipal principal, IUnitOfWork unitOfWork,
        ITripPlannerService planner, IRateLimitService rateLimiter, HttpContext httpContext)
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

        var city = await unitOfWork.Cities.GetByIdAsync(request.CityId);
        if (city == null) return CityNotFound();

        if (!SpotsNearCity(city, request))
            return BadRequestResponse(PlanTripRequestValidator.InvalidCoordinatesMessage, "BadRequest");

        return OkResponse(await planner.PlanAsync(city, request));
    }

    // Places must lie around the trip city, so the planner never routes arbitrary points.
    private static bool SpotsNearCity(City city, PlanTripRequest request)
    {
        if (city.Latitude == null || city.Longitude == null) return true;
        var lat = (double)city.Latitude.Value;
        var lon = (double)city.Longitude.Value;
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
        SaveTripRequest request, IValidator<SaveTripRequest> validator, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var city = await unitOfWork.Cities.GetByIdAsync(request.Plan.CityId);
        if (city == null) return CityNotFound();

        var plan = request.Plan;
        plan.DistrictId = city.DistrictId;
        plan.CityKey = city.CityKey;
        plan.CityName = city.Name;

        var now = DateTime.UtcNow;
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            CityId = city.Id,
            DistrictId = city.DistrictId,
            CityKey = city.CityKey,
            CityName = city.Name,
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
        var plan = request.Plan;
        plan.CityId = trip.CityId;
        plan.DistrictId = trip.DistrictId;
        plan.CityKey = trip.CityKey;
        plan.CityName = trip.CityName;

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

    private static TripSpotsPageDto EmptySpotsPage(string cityName, long? cityKey) => new()
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
        return string.IsNullOrEmpty(trimmed) ? $"{cityName} trip" : trimmed;
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
        CityId = trip.CityId,
        DistrictId = trip.DistrictId,
        CityKey = trip.CityKey,
        CityName = trip.CityName,
        DayCount = trip.DayCount,
        SpotCount = trip.SpotCount,
        Plan = plan,
        CreatedAt = trip.CreatedAt,
        UpdatedAt = trip.UpdatedAt,
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
