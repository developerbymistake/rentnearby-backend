using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;
using RentNearBy.Api.Validators;
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
    private const int SupplyMaxPerWindow = 60;
    private static readonly TimeSpan SupplyWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SupplyTtl = TimeSpan.FromHours(1);
    private const int MaxCitiesPageSize = 200;
    private const int MaxCitySearchLength = 100;
    private const int MaxStateLength = 100;
    private const double MaxSpotDistanceKm = 200;
    private const string ClientIdIndex = "ix_trips_owner_client";
    private const string StateUpcoming = "upcoming";
    private const string StateCompleted = "completed";

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
        HttpContext httpContext, string? q = null, int page = 1, int pageSize = 0, bool popular = false, string? state = null)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await CitiesRateLimitedAsync(userId, rateLimiter, httpContext) is { } limited) return limited;

        var pageNo = Math.Clamp(page, 1, MaxPage);
        var size = Math.Clamp(pageSize <= 0 ? options.Value.CitiesPageSize : pageSize, 1, MaxCitiesPageSize);
        var text = q?.Trim();
        if (text != null && text.Length > MaxCitySearchLength) return BadRequestResponse("Search text is too long", "BadRequest");
        var stateText = state?.Trim();
        if (stateText != null && stateText.Length > MaxStateLength) return BadRequestResponse("State is too long", "BadRequest");

        var snapshot = await catalog.GetAsync();
        if (snapshot == null) return CitiesUnavailable();

        var etag = $"W/\"{snapshot.Version}:{pageNo}:{size}\"";
        var unfiltered = string.IsNullOrEmpty(text) && !popular && string.IsNullOrEmpty(stateText);
        if (unfiltered) httpContext.Response.Headers.ETag = etag;
        if (unfiltered && httpContext.Request.Headers.IfNoneMatch.ToString() == etag)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        var matches = TripCityCatalog.FilterCities(snapshot.Cities, text, popular, stateText);
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

    // Stays accepting guests and drivers with a day rate in the city's district; cached per city for an hour.
    public static async Task<IResult> GetCitySupply(
        long cityKey, ClaimsPrincipal principal, ITripCityCatalog catalog, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IRateLimitService rateLimiter, IMemoryCache memory, IServiceProvider sp, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var rl = await rateLimiter.CheckAsync($"trip:supply:{userId}", SupplyMaxPerWindow, SupplyWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)(rl.RetryAfter ?? SupplyWindow).TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }
        if (cityKey <= 0) return CityNotFound();

        var key = TripCacheKeys.Supply(cityKey);
        var redis = sp.GetService<IConnectionMultiplexer>();
        if (await ReadSupplyAsync(redis, memory, key) is { } cached) return OkResponse(cached);

        var (available, city) = await catalog.FindAsync(cityKey);
        city ??= await unitOfWork.Trips.FindCitySnapshotAsync(userId, cityKey);
        if (city == null) return available ? CityNotFound() : CitiesUnavailable();

        var supply = new TripCitySupplyDto
        {
            DistrictId = await unitOfWork.Districts.FindActiveContainingAsync(city.Latitude, city.Longitude),
        };
        if (supply.DistrictId is { } districtId)
        {
            supply.Stays = await db.StayListings.AsNoTracking().CountAsync(s =>
                s.DistrictId == districtId && s.Status == StayStatuses.Approved && s.AcceptingGuests && !s.IsDeleted);
            supply.Drivers = await db.TaxiListings.AsNoTracking().CountAsync(t =>
                t.DistrictId == districtId && t.Status == TaxiStatuses.Approved && t.IsAvailable && !t.IsDeleted && t.LocalDayRate != null);
        }

        await WriteSupplyAsync(redis, memory, key, supply);
        return OkResponse(supply);
    }

    private static async Task<TripCitySupplyDto?> ReadSupplyAsync(IConnectionMultiplexer? redis, IMemoryCache memory, string key)
    {
        if (redis == null) return memory.TryGetValue(key, out TripCitySupplyDto? local) ? local : null;
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<TripCitySupplyDto>(value.ToString(), TripJson.Options);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            return null;
        }
    }

    private static async Task WriteSupplyAsync(IConnectionMultiplexer? redis, IMemoryCache memory, string key, TripCitySupplyDto supply)
    {
        if (redis == null)
        {
            memory.Set(key, supply, SupplyTtl);
            return;
        }
        try
        {
            await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(supply, TripJson.Options), SupplyTtl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Best effort: the next call counts again.
        }
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

    public static async Task<IResult> GetMine(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ISavedJourneyRepository journeys,
        int page = 1, int pageSize = DefaultPageSize, string? state = null)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var stateText = string.IsNullOrWhiteSpace(state) ? StateUpcoming : state.Trim().ToLowerInvariant();
        if (stateText is not (StateUpcoming or StateCompleted))
            return BadRequestResponse("State must be upcoming or completed");

        var pageNo = Math.Clamp(page, 1, MaxPage);
        var size = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var (items, hasMore) = await unitOfWork.Trips.GetByOwnerPagedAsync(userId, pageNo, size, stateText == StateCompleted);
        var (upcoming, completed) = await unitOfWork.Trips.GetStateCountsAsync(userId);

        var tripIds = items.Select(t => t.Id).ToList();
        var enquiries = await unitOfWork.TripEnquiries.GetByTripsAsync(tripIds);
        var byTrip = enquiries.Where(e => e.TripId != null).GroupBy(e => e.TripId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<TripEnquiry>)g.ToList());
        var journeysByTrip = await journeys.GetByTripsAsync(tripIds, userId);

        return OkResponse(new TripListDto
        {
            Items = items.Select(t => ToSummary(
                t,
                byTrip.TryGetValue(t.Id, out var list) ? list : Array.Empty<TripEnquiry>(),
                journeysByTrip.TryGetValue(t.Id, out var linked) ? linked : new List<SavedJourney>())).ToList(),
            HasMore = hasMore,
            Counts = new TripListCountsDto { Upcoming = upcoming, Completed = completed },
        });
    }

    public static async Task<IResult> Create(
        SaveTripRequest request, IValidator<SaveTripRequest> validator, ClaimsPrincipal principal, IUnitOfWork unitOfWork,
        ITripCityCatalog catalog, ISavedJourneyRepository journeys, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        // A replayed create answers with the row it already made, even when the city has gone off since.
        if (request.ClientTripId is { } clientId
            && await unitOfWork.Trips.FindByClientIdAsync(userId, clientId) is { } existing)
            return OkResponse(ToDto(existing, ReadPlan(existing)));

        // A new trip needs a live city; the server snapshot wins over the client copy.
        var (available, city) = await catalog.FindAsync(request.Plan.CityKey);
        if (!available) return CitiesUnavailable();
        if (city == null) return CityNotFound();

        var districtId = await unitOfWork.Districts.FindActiveContainingAsync(city.Latitude, city.Longitude);

        var plan = request.Plan;
        plan.CityKey = city.CityKey;
        plan.CityName = city.Name;
        plan.DistrictId = districtId;
        var linkIds = await SanitiseStepsAsync(plan, journeys, userId);

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
            ClientTripId = request.ClientTripId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ApplyPlan(trip, plan);

        await unitOfWork.Trips.AddAsync(trip);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (request.ClientTripId != null
            && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: ClientIdIndex })
        {
            // Lost the race to a parallel create with the same client id: answer with the winner.
            await unitOfWork.Trips.DeleteAsync(trip);
            var winner = await unitOfWork.Trips.FindByClientIdAsync(userId, request.ClientTripId.Value);
            if (winner == null) throw;
            return OkResponse(ToDto(winner, ReadPlan(winner)));
        }

        // The trip row must exist before a journey can point at it; a failure here is healed by the next save.
        if (linkIds.Count > 0)
        {
            try
            {
                await journeys.RelinkTripAsync(trip.Id, userId, linkIds);
            }
            catch (Exception ex) when (ex is DbUpdateException or NpgsqlException)
            {
                loggers.CreateLogger("Trips").LogWarning(ex, "trip.journeys.relink_failed {TripId}", trip.Id);
            }
        }

        return CreatedResponse(ToDto(trip, plan), $"/api/v1/trips/{trip.Id}");
    }

    public static async Task<IResult> GetById(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ISavedJourneyRepository journeys)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(id, userId);
        if (trip == null) return TripNotFound();

        await AdoptDistrictAsync(unitOfWork, trip);
        var dto = await BuildDtoAsync(unitOfWork, journeys, trip, userId, DateTime.UtcNow);
        dto.Plan.DistrictId ??= trip.DistrictId;
        return OkResponse(dto);
    }

    // A district that went live after the trip was saved is picked up on read too; a found district is never re-resolved.
    private static async Task AdoptDistrictAsync(IUnitOfWork unitOfWork, Trip trip)
    {
        if (trip.DistrictId != null) return;
        var districtId = await unitOfWork.Districts.FindActiveContainingAsync(trip.CityLatitude, trip.CityLongitude);
        if (districtId == null) return;
        trip.DistrictId = districtId;
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // A parallel save won; that save resolves the district itself.
        }
    }

    // The trip with its enquiries (newest first), its linked journeys and the derived booking state.
    internal static async Task<TripDto> BuildDtoAsync(
        IUnitOfWork unitOfWork, ISavedJourneyRepository journeys, Trip trip, Guid viewerId, DateTime nowUtc)
    {
        var plan = ReadPlan(trip);
        var enquiries = await unitOfWork.TripEnquiries.GetByTripAsync(trip.Id);
        var dto = ToDto(trip, plan, enquiries);
        dto.Enquiries = await TripEnquiryDtoBuilder.BuildManyAsync(unitOfWork, enquiries, viewerId, nowUtc);
        dto.Journeys = await JourneysOfAsync(journeys, trip, plan, viewerId);
        return dto;
    }

    internal static async Task<List<TripJourneyDto>> JourneysOfAsync(ISavedJourneyRepository journeys, Trip trip, TripPlan plan, Guid viewerId)
    {
        if (trip.OwnerUserId != viewerId) return new List<TripJourneyDto>();
        var linked = await journeys.GetByTripAsync(trip.Id, viewerId);
        if (linked.Count == 0) return new List<TripJourneyDto>();
        var taxis = await TripJourneyDtoBuilder.LoadTaxisAsync(journeys, linked);
        return TripJourneyDtoBuilder.BuildMany(linked, plan, taxis);
    }

    // Steps are trusted only for the caller's live journeys; returns the ids to link to the trip.
    private static async Task<List<Guid>> SanitiseStepsAsync(TripPlan plan, ISavedJourneyRepository journeys, Guid userId)
    {
        var referenced = TripStepRules.ReferencedIds(plan.Steps);
        var owned = referenced.Count == 0
            ? new HashSet<Guid>()
            : (await journeys.GetOwnedByIdsAsync(referenced, userId)).Select(j => j.Id).ToHashSet();
        return TripStepRules.Sanitise(plan.Steps, owned);
    }

    public static async Task<IResult> Update(
        Guid id, SaveTripRequest request, IValidator<SaveTripRequest> validator, ClaimsPrincipal principal, IUnitOfWork unitOfWork,
        ApplicationDbContext db, ITripChatService chat, ITripNotifier notifier, ILoggerFactory loggers,
        ISavedJourneyRepository journeys)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(id, userId);
        if (trip == null) return TripNotFound();

        // An older app would drop fields of a newer plan version.
        if (StoredPlanVersion(trip) > request.Plan.Version)
            return ConflictResponse("Update the app to edit this trip.", TripEnquiryErrorTypes.TripNeedsUpdate);
        if (trip.Status == TripStatuses.Completed)
            return ConflictResponse("This trip is complete. Plan changes are off.", TripEnquiryErrorTypes.TripCompleted);

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

        // An app that does not know steps sends none: the stored ones stay; on its date change their own dates go.
        var requestedSteps = plan.Steps;
        plan.Steps = TripStepsMerge.Resolve(ReadPlan(trip).Steps, requestedSteps);
        if (requestedSteps == null
            && (TripDateRules.StartMoved(trip.StartDate, plan.Settings.StartDate)
                || (trip.EndDate is { } storedEnd && plan.EndDate is { } nextEnd && nextEnd < storedEnd)))
            TripStepRules.ClearDates(plan.Steps);
        var linkIds = await SanitiseStepsAsync(plan, journeys, userId);

        if (request.Title != null) trip.Title = TitleOrDefault(request.Title, trip.CityName);
        if (request.Status != null) trip.Status = request.Status;
        var oldStart = trip.StartDate;
        var oldEnd = trip.EndDate;
        ApplyPlan(trip, plan);
        var now = DateTime.UtcNow;
        trip.UpdatedAt = now;

        // A new start date or an earlier end: unconfirmed asks with a night outside the trip can never get an offer,
        // so they close and the owner is told. Confirmed bookings keep their dates (D7); their pending revised offers
        // and change requests that fall outside can never be accepted, so they are superseded.
        var startMoved = TripDateRules.StartMoved(oldStart, trip.StartDate);
        var endCut = !startMoved && oldEnd is { } previousEnd && trip.EndDate is { } newEnd && newEnd < previousEnd;
        var closing = new List<TripEnquiry>();
        var staleOffers = new List<TripOffer>();
        var reopened = new List<TripEnquiry>();
        var reopenedOffers = new List<TripOffer>();
        if (startMoved || endCut)
        {
            var open = await db.TripEnquiries
                .Where(x => x.TripId == trip.Id && TripEnquiryStatuses.Withdrawable.Contains(x.Status))
                .ToListAsync();
            closing = open.Where(x => TripDateRules.OutsideWindow(trip.StartDate, trip.EndDate, x.CheckIn, x.CheckOut)).ToList();

            // An enquiry still inside the trip can hold an offer or counter for wider dates that can no longer be accepted.
            var negotiating = open
                .Where(x => !closing.Contains(x) && x.Status is TripEnquiryStatuses.Offered or TripEnquiryStatuses.Countered)
                .ToList();
            if (negotiating.Count > 0)
            {
                var negotiatingIds = negotiating.Select(x => x.Id).ToList();
                reopenedOffers = (await db.TripOffers
                        .Where(o => negotiatingIds.Contains(o.TripEnquiryId) && o.Status == TripOfferStatuses.Pending
                            && (o.Kind == TripOfferKinds.Offer || o.Kind == TripOfferKinds.Counter))
                        .ToListAsync())
                    .Where(o => o.CheckIn != null && o.CheckOut != null
                        && TripDateRules.OutsideWindow(trip.StartDate, trip.EndDate, o.CheckIn, o.CheckOut))
                    .ToList();
                reopened = negotiating.Where(x => reopenedOffers.Any(o => o.TripEnquiryId == x.Id)).ToList();
            }
            var bookedIds = await db.TripEnquiries
                .Where(x => x.TripId == trip.Id && TripEnquiryStatuses.Booked.Contains(x.Status))
                .Select(x => x.Id)
                .ToListAsync();
            if (bookedIds.Count > 0)
                staleOffers = (await db.TripOffers
                        .Where(o => bookedIds.Contains(o.TripEnquiryId) && o.Status == TripOfferStatuses.Pending
                            && (o.Kind == TripOfferKinds.Revised || o.Kind == TripOfferKinds.ChangeRequest))
                        .ToListAsync())
                    .Where(o => o.CheckIn != null && o.CheckOut != null
                        && TripDateRules.OutsideWindow(trip.StartDate, trip.EndDate, o.CheckIn, o.CheckOut))
                    .ToList();
        }
        var posts = new List<TripChatPost>();
        var pushes = new List<TripPendingPush>();

        await unitOfWork.BeginTransactionAsync();
        try
        {
            foreach (var x in closing)
            {
                x.Status = TripEnquiryStatuses.Closed;
                x.FailReason = TripCloseReasons.TripDatesChanged;
                x.UpdatedAt = now;
                await TripEnquiryOps.CloseOpenOfferAsync(db, chat, x, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, posts, now);
                await TripEnquiryOps.PostSystemAsync(db, chat, x, userId, TripSystemLines.TripDatesChanged, posts);
                pushes.Add(new TripPendingPush(x, x.OwnerUserId,
                    endCut
                        ? (Func<TripEnquiry, TripPushContext, NotificationEvent>)TripEnquiryHandlers.EnquiryClosedByDaysCut
                        : TripEnquiryHandlers.EnquiryClosedByDateChange));
            }
            foreach (var o in staleOffers)
            {
                o.Status = TripOfferStatuses.Superseded;
                o.RespondedAt ??= now;
                if (o.MessageId is { } messageId && await chat.RewriteStatusAsync(messageId, TripCardStatuses.Superseded) is { } rewrite)
                    posts.Add(rewrite);
            }
            foreach (var o in reopenedOffers)
            {
                o.Status = TripOfferStatuses.Superseded;
                o.RespondedAt ??= now;
                if (o.MessageId is { } messageId && await chat.RewriteStatusAsync(messageId, TripCardStatuses.Superseded) is { } rewrite)
                    posts.Add(rewrite);
            }
            foreach (var x in reopened)
            {
                x.Status = TripEnquiryStatuses.Asked;
                x.UpdatedAt = now;
                pushes.Add(new TripPendingPush(x, x.OwnerUserId, endCut
                    ? (Func<TripEnquiry, TripPushContext, NotificationEvent>)OfferOutsideShortenedTrip
                    : OfferOutsideMovedTrip));
            }
            await journeys.RelinkTripAsync(trip.Id, userId, linkIds);
            await unitOfWork.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return ConflictResponse("This trip was just modified. Please retry.", "CONCURRENT_UPDATE");
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await TripEnquiryHandlers.AfterCommitAsync(chat, posts, pushes, unitOfWork, notifier, loggers);
        var dto = ToDto(trip, plan, await unitOfWork.TripEnquiries.GetByTripAsync(trip.Id));
        dto.Journeys = await JourneysOfAsync(journeys, trip, plan, userId);
        return OkResponse(dto);
    }

    // The host's offer (or the guest's counter) no longer fits the trip: the enquiry is back to Asked.
    private static NotificationEvent OfferOutsideMovedTrip(TripEnquiry e, TripPushContext ctx)
        => OfferOutsideTrip(e, ctx, "changed the trip dates");

    private static NotificationEvent OfferOutsideShortenedTrip(TripEnquiry e, TripPushContext ctx)
        => OfferOutsideTrip(e, ctx, "shortened the trip");

    private static NotificationEvent OfferOutsideTrip(TripEnquiry e, TripPushContext ctx, string what)
    {
        var n = TripNotifications.TripEnquiryReceived(e, ctx);
        var who = string.IsNullOrWhiteSpace(ctx.TravellerName) ? "The guest" : ctx.TravellerName.Trim();
        n.Title = (e.IsUrgent ? "Urgent · " : string.Empty) + "Send a new offer";
        n.Body = $"{who} {what}. Offer again for {TripNotifications.Dates(e)}.";
        return n;
    }

    // Open enquiries close first (confirmed ones count as a guest cancel); each owner told once after commit.
    // Linked journeys are unlinked, never deleted.
    public static async Task<IResult> Delete(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ITripEnquiryClosure closure, ISavedJourneyRepository journeys)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(id, userId);
        if (trip == null) return TripNotFound();

        IReadOnlyList<TripClosurePush> pushes;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            pushes = await closure.OnTripDeletedAsync(trip.Id);
            await journeys.UnlinkTripAsync(trip.Id);
            await unitOfWork.Trips.DeleteAsync(trip);
            await unitOfWork.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return ConflictResponse("This trip was just modified. Please retry.", "CONCURRENT_UPDATE");
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await closure.AfterCommitAsync(pushes);
        var ownersTold = pushes.Select(p => p.Notification.TargetUserId).Where(t => t != null).Distinct().Count();
        return OkResponse(new { message = "Trip deleted", ownersTold });
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
        plan.Version = TripPlan.CurrentVersion;
        trip.PlanJson = JsonSerializer.Serialize(plan, TripJson.Options);
        trip.DayCount = plan.Days.Count;
        trip.SpotCount = plan.Days.Sum(d => d.Spots.Count);
        trip.StartDate = plan.Settings.StartDate;
        trip.EndDate = plan.EndDate;
    }

    // Version of the stored plan only, so a document a newer app wrote is still recognised.
    internal static int StoredPlanVersion(Trip trip)
    {
        try
        {
            using var doc = JsonDocument.Parse(trip.PlanJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("version", out var v) && v.TryGetInt32(out var n) ? n : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    internal static TripPlan ReadPlan(Trip trip)
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

    private static TripDto ToDto(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry>? enquiries = null) => new()
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
        StartDate = trip.StartDate,
        EndDate = trip.EndDate,
        Plan = plan,
        BookingState = TripBookingStates.Compute(trip, plan, enquiries ?? Array.Empty<TripEnquiry>()),
        MutedAt = trip.MutedAt,
        CompletedAt = trip.CompletedAt,
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
        IsPopularDestination = c.IsPopularDestination,
        Status = c.Status,
        SpotCount = c.SpotCount,
    };

    private static TripSummaryDto ToSummary(Trip trip, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney> journeys)
    {
        var plan = ReadPlan(trip);
        var counts = TripBookingSummary.Count(plan, enquiries);
        return new TripSummaryDto
        {
            Id = trip.Id,
            Title = trip.Title,
            Status = trip.Status,
            CityName = trip.CityName,
            DayCount = trip.DayCount,
            SpotCount = trip.SpotCount,
            TaxiDays = plan.Days.Count(d => d.Taxi != null && d.Taxi.Skipped != true),
            StayDays = plan.Days.Count(d => d.Stay != null),
            StayName = plan.Days.Select(d => d.Stay?.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            Adults = plan.Settings?.Adults,
            Kids = plan.Settings?.Kids,
            Category = plan.Days.SelectMany(d => d.Spots).Select(s => s.Category).FirstOrDefault() ?? "attraction",
            BookingState = TripBookingStates.Compute(trip, plan, enquiries),
            StaysConfirmed = counts.StaysConfirmed,
            StaysTotal = counts.StaysTotal,
            TaxisConfirmed = counts.TaxisConfirmed,
            TaxisTotal = counts.TaxisTotal,
            StaysOwn = counts.StaysOwn,
            TaxisOwn = counts.TaxisOwn,
            AgreedTotal = counts.AgreedTotal,
            GettingThere = TripJourneyDtoBuilder.DisplayOf(plan, TripStepRoles.GettingThere, journeys),
            ComingBack = TripJourneyDtoBuilder.DisplayOf(plan, TripStepRoles.ComingBack, journeys),
            MutedAt = trip.MutedAt,
            CompletedAt = trip.CompletedAt,
            UpdatedAt = trip.UpdatedAt,
        };
    }
}
