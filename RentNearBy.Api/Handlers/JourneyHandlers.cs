using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Api.Validators;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Saved journeys of /api/v1/taxis/journeys. Owner-only rows (a foreign id answers 404), logged-in callers only.
public static class JourneyHandlers
{
    private static readonly TimeSpan SaveWindow = TimeSpan.FromMinutes(10);
    private const int SavePerWindow = 20;

    private static IResult Invalid(FluentValidation.Results.ValidationResult v)
        => v.Errors[0].ErrorMessage == TaxiWaysRules.DateMessage
            ? BadRequestResponse(TaxiWaysRules.DateMessage, TaxiWaysErrorTypes.InvalidDate)
            : BadRequestResponse(v.Errors[0].ErrorMessage);

    internal static IResult NotFound()
        => ErrorResponse(404, "Journey not found", JourneyErrorTypes.JourneyNotFound);

    private static IResult Concurrent()
        => ConflictResponse("This journey just changed.", JourneyErrorTypes.ConcurrentUpdate);

    // ---------- POST /taxis/journeys ----------

    public static async Task<IResult> Save(
        SaveJourneyRequest request, IValidator<SaveJourneyRequest> validator, ClaimsPrincipal principal,
        ISavedJourneyRepository journeys, IUnitOfWork unitOfWork, IRateLimitService rateLimiter, ITaxiWaysSearch ways, HttpContext http,
        ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger("Journeys");
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var limited = await rateLimiter.CheckAsync($"taxi:journey:save:{userId}", SavePerWindow, SaveWindow);
        if (!limited.IsAllowed)
        {
            http.Response.Headers["Retry-After"] = ((int)(limited.RetryAfter ?? SaveWindow).TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        // Travel between two cities of a trip: checked and answered with its trip role, but never linked here. The plan save
        // writes the arrival and TripId together (RelinkTripAsync), so a link the app does not finish can never strand it.
        TripLinkTarget? target = null;
        if (request.TripId is { } requestTripId)
        {
            var (resolved, error) = await ResolveTargetAsync(unitOfWork, requestTripId, request, userId);
            if (error != null) return error;
            target = resolved;
        }

        // A double tap runs find-then-insert twice; one save per user at a time makes the second one find the first.
        var gate = SaveGates[(userId.GetHashCode() & int.MaxValue) % SaveGates.Length];
        await gate.WaitAsync();
        try
        {
            return await SaveLockedAsync(request, userId, journeys, unitOfWork, ways, log, target);
        }
        finally
        {
            gate.Release();
        }
    }

    private static readonly SemaphoreSlim[] SaveGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private static async Task<IResult> SaveLockedAsync(
        SaveJourneyRequest request, Guid userId, ISavedJourneyRepository journeys, IUnitOfWork unitOfWork, ITaxiWaysSearch ways,
        ILogger log, TripLinkTarget? target)
    {
        // Same plan, date and people = the same journey; a people change is a new journey (D24). A completed one is never reused.
        var existing = await journeys.FindByPlanAsync(userId, request.PlanKey, request.Date);
        if (existing != null && existing.People != request.People)
            existing = (await journeys.GetLiveByUserAsync(userId))
                .FirstOrDefault(j => j.Status != JourneyStatuses.Completed
                    && j.PlanKey == request.PlanKey && j.TravelDate == request.Date && j.People == request.People);
        if (existing != null)
        {
            if (target != null)
            {
                if (existing.TripId is { } linkedTrip && linkedTrip != target.Trip.Id)
                {
                    if (await IsInPlanAsync(unitOfWork, linkedTrip, existing.Id, userId))
                        return ErrorResponse(409, "This journey is in another trip", JourneyErrorTypes.JourneyInTrip);
                    await journeys.SetTripAsync(existing.Id, null);
                    existing.TripId = null;
                }
                if (TripJourneyLinks.Resolve(target.Plan, existing.Id) is { } link
                    && (link.Role != TripStepRoles.Segment || link.LegIndex != target.LegIndex))
                    return ErrorResponse(409, "This journey is already in your trip", JourneyErrorTypes.JourneyInTrip);
            }
            return OkResponse(await BuildSavedAsync(journeys, unitOfWork, userId, existing, log, target));
        }

        if (await journeys.CountLiveAsync(userId) >= JourneyRules.MaxLive)
            return ConflictResponse("Up to 20 saved journeys.", JourneyErrorTypes.JourneyLimitReached);

        var legs = request.Legs.OrderBy(l => l.LegIndex).ToList();
        var ids = legs
            .SelectMany(l => new[] { l.DriverTaxiId, l.Backup1TaxiId ?? Guid.Empty, l.Backup2TaxiId ?? Guid.Empty }.Concat(l.CoverTaxiIds ?? []))
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var taxis = await journeys.LoadTaxisAsync(ids);

        var planKeys = TaxiWaysKeys.TryParsePlanKey(request.PlanKey, out var parsedKeys) ? parsedKeys : new List<string>();
        var firstKey = planKeys.Count > 0 ? planKeys[0] : null;
        var lastKey = planKeys.Count > 0 ? planKeys[^1] : null;

        var now = DateTime.UtcNow;
        var journey = new SavedJourney
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FromName = TaxiWaysLabels.Of(request.FromName, firstKey, true, false),
            FromLatitude = (decimal)request.FromLat,
            FromLongitude = (decimal)request.FromLng,
            ToName = TaxiWaysLabels.Of(request.ToName, lastKey, false, true),
            ToLatitude = (decimal)request.ToLat,
            ToLongitude = (decimal)request.ToLng,
            TravelDate = request.Date,
            People = (short)request.People,
            PlanKey = request.PlanKey,
            Cars = (short)legs.Count,
            CreatedAt = now,
            UpdatedAt = now,
        };

        IResult? legError;
        try
        {
            legError = await BuildLegsAsync(journey, legs, request, taxis, userId, now, ways, journeys);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "journey.save.legs.failed {UserId} {PlanKey} {Legs}", userId, request.PlanKey, legs.Count);
            return ErrorResponse(409, "Could not price this journey. Please try again.", JourneyErrorTypes.DriverNotOnLeg);
        }
        if (legError != null) return legError;

        journey.ListedTotal = Total(journey.Legs);
        journey.Status = JourneyRules.JourneyStatusOf(journey.Legs);

        await journeys.AddAsync(journey);
        try
        {
            await journeys.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Concurrent();
        }
        catch (DbUpdateException ex)
        {
            log.LogError(ex, "journey.save.failed {UserId} {PlanKey} {Date} {Legs}", userId, request.PlanKey, request.Date, legs.Count);
            return ErrorResponse(409, "Could not save this journey. Please try again.", JourneyErrorTypes.ConcurrentUpdate);
        }
        return CreatedResponse(await BuildSavedAsync(journeys, unitOfWork, userId, journey, log, target), $"/api/v1/taxis/journeys/{journey.Id}");
    }

    // The journey is committed: a failed read model never turns the save into a 500; the plain journey answers.
    private static async Task<JourneyDto> BuildSavedAsync(
        ISavedJourneyRepository journeys, IUnitOfWork unitOfWork, Guid userId, SavedJourney journey, ILogger log, TripLinkTarget? target)
    {
        try
        {
            return await BuildAsync(journeys, unitOfWork, userId, journey, new HashSet<int>(), log, target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "journey.save.build.failed {JourneyId}", journey.Id);
            var dto = Map(journey, new Dictionary<Guid, JourneyTaxiInfo>(), new HashSet<int>());
            if (target != null)
            {
                dto.TripId = target.Trip.Id;
                dto.TripTitle = target.Trip.Title;
                dto.TripRole = TripStepRoles.Segment;
                dto.TripLegIndex = target.LegIndex;
                dto.TripTravelDay = target.TravelDay;
            }
            return dto;
        }
    }

    private static async Task<IResult?> BuildLegsAsync(
        SavedJourney journey, List<SaveJourneyLegRequest> legs, SaveJourneyRequest request, Dictionary<Guid, JourneyTaxiInfo> taxis,
        Guid userId, DateTime now, ITaxiWaysSearch ways, ISavedJourneyRepository journeys)
    {
        Guid? previousRoute = null;
        foreach (var l in legs)
        {
            if (!taxis.TryGetValue(l.DriverTaxiId, out var driver) || !driver.IsPublic || driver.UserId == userId)
                return ErrorResponse(409, "A driver is not available any more.", JourneyErrorTypes.DriverNotOnLeg);

            var backups = new[] { l.Backup1TaxiId, l.Backup2TaxiId }
                .Where(b => b is { } g && g != l.DriverTaxiId && taxis.TryGetValue(g, out var t) && t.IsPublic && t.UserId != userId)
                .Select(b => b!.Value)
                .Distinct()
                .ToList();

            // Priced only on the server (the network's link, else the stored route), and the seat cover is re-solved over
            // the driver and the cover cars the traveller accepted. The client's fare is never stored.
            var coverIds = (l.CoverTaxiIds ?? [])
                .Where(c => c != l.DriverTaxiId && taxis.TryGetValue(c, out var t) && t.IsPublic && t.UserId != userId)
                .Distinct()
                .ToList();
            var fares = await JourneyLegFares.LoadAsync(
                ways, journeys, l.LegKey, l.FromLat, l.FromLng, l.ToLat, l.ToLng, coverIds.Prepend(l.DriverTaxiId), taxis, previousRoute,
                l.LegIndex == legs[0].LegIndex, l.LegIndex == legs[^1].LegIndex);
            if (JourneyLegFares.Quote(l.DriverTaxiId, coverIds, fares, request.People) is not { } quote)
                return ErrorResponse(409, "This driver does not go on this stretch.", JourneyErrorTypes.DriverNotOnLeg);
            var driverFare = fares[l.DriverTaxiId];
            previousRoute = driverFare.RouteId;

            TaxiWaysKeys.TryParseLegKey(l.LegKey, out var legFrom, out var legTo);
            journey.Legs.Add(new SavedJourneyLeg
            {
                Id = Guid.NewGuid(),
                JourneyId = journey.Id,
                LegIndex = (short)l.LegIndex,
                LegKey = l.LegKey,
                FromName = TaxiWaysLabels.Of(l.FromName, legFrom, l.LegIndex == 1, false),
                FromLatitude = (decimal)l.FromLat,
                FromLongitude = (decimal)l.FromLng,
                ToName = TaxiWaysLabels.Of(l.ToName, legTo, false, l.LegIndex == legs.Count),
                ToLatitude = (decimal)l.ToLat,
                ToLongitude = (decimal)l.ToLng,
                MeetingPlace = string.IsNullOrWhiteSpace(l.MeetingPlace) ? null : TripTextSafety.Clean(l.MeetingPlace),
                DriverTaxiId = l.DriverTaxiId,
                DriverRouteId = driverFare.RouteId,
                ListedFare = quote.Fare,
                Partial = driverFare.Partial,
                CoverTaxiIds = quote.CoverOthers.ToArray(),
                Backup1TaxiId = backups.Count > 0 ? backups[0] : null,
                Backup2TaxiId = backups.Count > 1 ? backups[1] : null,
                LastKnownStatus = JourneyLegStatuses.Unknown,
                DriverAvailable = true,
                UpdatedAt = now,
            });
        }

        return null;
    }

    private sealed record TripLinkTarget(Trip Trip, TripPlan Plan, int LegIndex, int? TravelDay);

    // Owned trip, not completed, leg, route ends. The date is the one the app computed from its plan, which may be ahead of
    // the stored row (dates or days edited, autosave pending): it is taken as sent. The plan save links; the travel tile
    // and the ask compare the journey's date with the saved trip (TravelDateChanged there).
    private static async Task<(TripLinkTarget? Target, IResult? Error)> ResolveTargetAsync(
        IUnitOfWork unitOfWork, Guid tripId, SaveJourneyRequest request, Guid userId)
    {
        var trip = (await unitOfWork.Trips.GetByIdsForOwnerAsync([tripId], userId)).FirstOrDefault();
        if (trip == null) return (null, ErrorResponse(404, "Trip not found", TripErrorTypes.TripNotFound));
        if (trip.Status == TripStatuses.Completed) return (null, BadRequestResponse("This trip is completed"));

        var plan = TripPlanReader.Read(trip);
        var legIndex = request.LegIndex ?? 0;
        if (legIndex < 2) return (null, BadRequestResponse(SaveJourneyRequestValidator.UnknownCityMessage));
        var legs = plan.Legs ?? new List<TripCityLeg>();
        // A city added and not saved yet has no stored leg: nothing stored to check it against.
        if (legIndex > legs.Count) return (new TripLinkTarget(trip, plan, legIndex, null), null);

        var leg = legs[legIndex - 1];
        var previous = legs[legIndex - 2];
        if (GeoMath.HaversineKm(request.ToLat, request.ToLng, leg.Latitude, leg.Longitude) > RouteMatchKm
            || GeoMath.HaversineKm(request.FromLat, request.FromLng, previous.Latitude, previous.Longitude) > RouteMatchKm)
            return (null, BadRequestResponse("This journey is for another route", JourneyErrorTypes.RouteMismatch));

        var travelDay = plan.LegStart(legIndex);
        return (new TripLinkTarget(trip, plan, legIndex, travelDay > 0 ? travelDay : null), null);
    }

    private static async Task<bool> IsInPlanAsync(IUnitOfWork unitOfWork, Guid tripId, Guid journeyId, Guid userId)
    {
        var trip = (await unitOfWork.Trips.GetByIdsForOwnerAsync([tripId], userId)).FirstOrDefault();
        return trip != null && TripJourneyLinks.Resolve(TripPlanReader.Read(trip), journeyId) != null;
    }

    private const double RouteMatchKm = 50;

    // ---------- GET /taxis/journeys ----------

    public static async Task<IResult> List(ClaimsPrincipal principal, ISavedJourneyRepository journeys, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var rows = await journeys.GetLiveByUserAsync(userId);
        var taxis = await LoadTaxisAsync(journeys, rows);
        var trips = await LoadTripsAsync(unitOfWork, rows, userId);
        var tripIds = rows.Where(j => j.TripId != null).Select(j => j.TripId!.Value).Distinct().ToList();
        var tripRows = tripIds.Count == 0 ? Array.Empty<TripEnquiry>() : await unitOfWork.TripEnquiries.GetByTripsAsync(tripIds);
        return OkResponse(new JourneyListDto
        {
            Items = rows.Select(j => WithTrip(Map(j, taxis, new HashSet<int>(), tripRows), j, trips, null)).ToList(),
        });
    }

    // ---------- GET /taxis/journeys/{id} ----------

    public static async Task<IResult> Get(
        Guid id, ClaimsPrincipal principal, ISavedJourneyRepository journeys, IUnitOfWork unitOfWork, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var journey = await journeys.GetOwnedAsync(id, userId, tracked: false);
        if (journey == null) return NotFound();
        return OkResponse(await BuildAsync(journeys, unitOfWork, userId, journey, new HashSet<int>(), loggers.CreateLogger("Journeys")));
    }

    // ---------- DELETE /taxis/journeys/{id} ----------

    // A journey with booked cars in a trip stays until they are cancelled; open asks of its trip travel are withdrawn.
    public static async Task<IResult> Delete(
        Guid id, ClaimsPrincipal principal, ISavedJourneyRepository journeys, IUnitOfWork unitOfWork,
        ITripNotifier notifier, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var journey = await journeys.GetOwnedAsync(id, userId, tracked: true);
        if (journey == null) return NotFound();

        var now = DateTime.UtcNow;
        var pushes = new List<TripPendingPush>();
        if (journey.TripId != null)
        {
            var rows = await journeys.GetSegmentRowsAsync(journey.Id);
            if (rows.Any(r => TripEnquiryStatuses.IsBooked(r.Status)))
                return ErrorResponse(409, "Cancel the booked cars in your trip first", JourneyErrorTypes.JourneyInTrip);
            foreach (var row in rows.Where(r => TripEnquiryStatuses.IsWithdrawable(r.Status)))
            {
                row.Status = TripEnquiryStatuses.Closed;
                row.FailReason = TripCloseReasons.Withdrawn;
                row.UpdatedAt = now;
                pushes.Add(new TripPendingPush(row, row.OwnerUserId, TripEnquiryHandlers.EnquiryWithdrawn));
            }
        }

        journey.IsDeleted = true;
        journey.TripId = null;
        journey.UpdatedAt = now;
        try
        {
            await journeys.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Concurrent();
        }

        if (pushes.Count > 0)
            await TripPushSender.SendAsync(pushes, unitOfWork.TripEnquiries, notifier, loggers.CreateLogger("Journeys"));
        return NoContentResponse();
    }

    // ---------- POST /taxis/journeys/{id}/refresh ----------

    public static async Task<IResult> Refresh(
        Guid id, ClaimsPrincipal principal, ISavedJourneyRepository journeys, IUnitOfWork unitOfWork, IRateLimitService rateLimiter,
        ITaxiWaysSearch ways, HttpContext http, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();

        var limited = await rateLimiter.CheckAsync($"taxi:journey:refresh:{userId}", JourneyRules.RefreshPerWindow, JourneyRules.RefreshWindow);
        if (!limited.IsAllowed)
        {
            http.Response.Headers["Retry-After"] = ((int)(limited.RetryAfter ?? JourneyRules.RefreshWindow).TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var journey = await journeys.GetOwnedAsync(id, userId, tracked: true);
        if (journey == null) return NotFound();

        var fareChanged = new HashSet<int>();
        if (journey.Status != JourneyStatuses.Completed)
        {
            var now = DateTime.UtcNow;
            var taxiIds = journey.Legs
                .SelectMany(LegTaxiIds)
                .ToHashSet();
            var taxis = await journeys.LoadTaxisAsync(taxiIds);
            var enquiries = await journeys.LoadEnquiriesAsync(journey.Legs.Where(l => l.LastEnquiryId != null).Select(l => l.LastEnquiryId!.Value).ToList());

            Guid? previousRoute = null;
            foreach (var leg in journey.Legs.OrderBy(l => l.LegIndex))
            {
                await RefreshLegAsync(ways, journeys, leg, taxis, enquiries, fareChanged, journey.People, previousRoute, journey.Legs);
                previousRoute = leg.DriverRouteId;
                leg.UpdatedAt = now;
            }

            journey.ListedTotal = Total(journey.Legs);
            journey.Status = JourneyRules.JourneyStatusOf(journey.Legs, await BookedLegsAsync(journeys, journey.Id));
            journey.LastRefreshedAt = now;
            journey.UpdatedAt = now;
        }
        else
        {
            journey.LastRefreshedAt = DateTime.UtcNow;
        }

        try
        {
            await journeys.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Concurrent();
        }
        return OkResponse(await BuildAsync(journeys, unitOfWork, userId, journey, fareChanged, loggers.CreateLogger("Journeys")));
    }

    // Every taxi a saved leg names: driver, backups and the other cars of its seat cover.
    private static IEnumerable<Guid> LegTaxiIds(SavedJourneyLeg l)
        => new[] { l.DriverTaxiId, l.Backup1TaxiId ?? Guid.Empty, l.Backup2TaxiId ?? Guid.Empty }
            .Concat(l.CoverTaxiIds)
            .Where(g => g != Guid.Empty);

    // The cover is re-solved over the driver and the cover cars still listed on the stretch, with live server fares.
    private static async Task RefreshLegAsync(
        ITaxiWaysSearch ways, ISavedJourneyRepository journeys, SavedJourneyLeg leg, Dictionary<Guid, JourneyTaxiInfo> taxis,
        Dictionary<Guid, TripEnquiry> enquiries, HashSet<int> fareChanged, int people, Guid? previousRoute,
        IEnumerable<SavedJourneyLeg> journeyLegs)
    {
        var driverLive = taxis.TryGetValue(leg.DriverTaxiId, out var driver) && driver.IsPublic;
        if (driverLive)
        {
            var cover = leg.CoverTaxiIds.Where(c => c != leg.DriverTaxiId && taxis.TryGetValue(c, out var t) && t.IsPublic).ToList();
            var fares = await JourneyLegFares.LoadAsync(ways, journeys, leg, cover.Prepend(leg.DriverTaxiId), taxis, journeyLegs, previousRoute);
            if (JourneyLegFares.Quote(leg.DriverTaxiId, cover, fares, people) is { } quote)
            {
                if (leg.ListedFare != quote.Fare)
                {
                    if (leg.ListedFare != null) fareChanged.Add(leg.LegIndex);
                    leg.ListedFare = quote.Fare;
                }
                leg.DriverRouteId = fares[leg.DriverTaxiId].RouteId;
                leg.CoverTaxiIds = quote.CoverOthers.ToArray();
            }
            // A pass-through stretch has no end-to-end route until its road line exists: never mark it unavailable for that.
            else if (!leg.Partial)
            {
                driverLive = false;
            }
        }
        leg.DriverAvailable = driverLive;

        var backups = new[] { leg.Backup1TaxiId, leg.Backup2TaxiId }
            .Where(b => b is { } g && g != leg.DriverTaxiId && taxis.TryGetValue(g, out var t) && t.IsPublic)
            .Select(b => b!.Value)
            .Distinct()
            .ToList();
        if (backups.Count < JourneyRules.Backups)
        {
            var exclude = backups.Append(leg.DriverTaxiId).ToList();
            var more = await journeys.MatchLegAsync(
                (double)leg.FromLatitude, (double)leg.FromLongitude, (double)leg.ToLatitude, (double)leg.ToLongitude,
                exclude, JourneyRules.Backups - backups.Count);
            backups.AddRange(more.Select(m => m.TaxiId).Take(JourneyRules.Backups - backups.Count));
        }
        leg.Backup1TaxiId = backups.Count > 0 ? backups[0] : null;
        leg.Backup2TaxiId = backups.Count > 1 ? backups[1] : null;

        if (leg.LastEnquiryId is { } enquiryId && enquiries.TryGetValue(enquiryId, out var e))
            JourneyLegState.Of(e).ApplyTo(leg);
    }

    // ---------- PUT /taxis/journeys/{id}/legs/{legIndex}/driver ----------

    public static async Task<IResult> ReplaceDriver(
        Guid id, int legIndex, ReplaceJourneyDriverRequest request, IValidator<ReplaceJourneyDriverRequest> validator,
        ClaimsPrincipal principal, ISavedJourneyRepository journeys, IUnitOfWork unitOfWork, ITaxiWaysSearch ways, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var journey = await journeys.GetOwnedAsync(id, userId, tracked: true);
        if (journey == null) return NotFound();
        if (journey.Status == JourneyStatuses.Completed) return BadRequestResponse("This journey is completed");
        var leg = journey.Legs.FirstOrDefault(l => l.LegIndex == legIndex);
        if (leg == null) return BadRequestResponse("Unknown leg");

        if (leg.DriverTaxiId != request.TaxiId)
        {
            var bookedLegs = await BookedLegsAsync(journeys, journey.Id);
            if (bookedLegs.Contains(legIndex))
                return ErrorResponse(409, "Cancel this booked car in your trip first", JourneyErrorTypes.JourneyInTrip);

            var taxis = await journeys.LoadTaxisAsync([request.TaxiId, leg.DriverTaxiId, .. leg.CoverTaxiIds]);
            if (!taxis.TryGetValue(request.TaxiId, out var next) || !next.IsPublic || next.UserId == userId)
                return ErrorResponse(409, "This driver is not available.", JourneyErrorTypes.DriverNotOnLeg);

            // The new driver takes the old one's car in the cover; the other cover cars stay and the cover is re-solved.
            var cover = leg.CoverTaxiIds
                .Where(c => c != request.TaxiId && c != leg.DriverTaxiId && taxis.TryGetValue(c, out var t) && t.IsPublic)
                .ToList();
            var previousRoute = journey.Legs.FirstOrDefault(l => l.LegIndex == legIndex - 1)?.DriverRouteId;
            var fares = await JourneyLegFares.LoadAsync(ways, journeys, leg, cover.Prepend(request.TaxiId), taxis, journey.Legs, previousRoute);
            if (JourneyLegFares.Quote(request.TaxiId, cover, fares, journey.People) is not { } quote)
                return ErrorResponse(409, "This driver does not go on this stretch.", JourneyErrorTypes.DriverNotOnLeg);

            var oldId = leg.DriverTaxiId;
            var oldLive = taxis.TryGetValue(oldId, out var old) && old.IsPublic;
            var backups = new List<Guid>();
            if (oldLive) backups.Add(oldId);
            backups.AddRange(new[] { leg.Backup1TaxiId, leg.Backup2TaxiId }.Where(b => b is { } g && g != request.TaxiId).Select(b => b!.Value));

            var now = DateTime.UtcNow;
            leg.DriverTaxiId = request.TaxiId;
            leg.DriverRouteId = fares[request.TaxiId].RouteId;
            leg.ListedFare = quote.Fare;
            leg.CoverTaxiIds = quote.CoverOthers.ToArray();
            leg.Partial = fares[request.TaxiId].Partial;
            leg.Backup1TaxiId = backups.Count > 0 ? backups[0] : null;
            leg.Backup2TaxiId = backups.Count > 1 ? backups[1] : null;
            leg.LastEnquiryId = null;
            leg.LastKnownStatus = JourneyLegStatuses.Unknown;
            leg.LastQuotedFare = null;
            leg.LastKnownAt = null;
            leg.DriverAvailable = true;
            leg.UpdatedAt = now;

            journey.ListedTotal = Total(journey.Legs);
            journey.Status = JourneyRules.JourneyStatusOf(journey.Legs, bookedLegs);
            journey.UpdatedAt = now;
            try
            {
                await journeys.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Concurrent();
            }
            catch (DbUpdateException ex)
            {
                loggers.CreateLogger("Journeys").LogError(ex, "journey.replacedriver.failed {JourneyId} {LegIndex}", id, legIndex);
                return ErrorResponse(409, "Could not change the driver. Please try again.", JourneyErrorTypes.ConcurrentUpdate);
            }
        }
        return OkResponse(await BuildAsync(journeys, unitOfWork, userId, journey, new HashSet<int>(), loggers.CreateLogger("Journeys")));
    }

    // Cars of this journey booked in a trip: their driver is never swapped and a pause never makes the journey DriverUnavailable.
    private static async Task<HashSet<int>> BookedLegsAsync(ISavedJourneyRepository journeys, Guid journeyId)
        => (await journeys.GetSegmentRowsAsync(journeyId))
            .Where(r => TripEnquiryStatuses.IsBooked(r.Status))
            .Select(r => TripEnquiryContexts.Read(r.ContextJson).LegIndex)
            .Where(i => i != null)
            .Select(i => i!.Value)
            .ToHashSet();

    // ---------- PUT /taxis/journeys/{id}/status ----------

    public static async Task<IResult> SetStatus(
        Guid id, JourneyStatusRequest request, IValidator<JourneyStatusRequest> validator, ClaimsPrincipal principal,
        ISavedJourneyRepository journeys, IUnitOfWork unitOfWork, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var journey = await journeys.GetOwnedAsync(id, userId, tracked: true);
        if (journey == null) return NotFound();
        if (journey.Status != JourneyStatuses.Completed)
        {
            journey.Status = JourneyStatuses.Completed;
            journey.UpdatedAt = DateTime.UtcNow;
            try
            {
                await journeys.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Concurrent();
            }
        }
        return OkResponse(await BuildAsync(journeys, unitOfWork, userId, journey, new HashSet<int>(), loggers.CreateLogger("Journeys")));
    }

    // ---------- mapping ----------

    private static int? Total(IEnumerable<SavedJourneyLeg> legs)
    {
        var fares = legs.Where(l => l.ListedFare != null).Select(l => l.ListedFare!.Value).ToList();
        return fares.Count == 0 ? null : fares.Sum();
    }

    private static async Task<Dictionary<Guid, JourneyTaxiInfo>> LoadTaxisAsync(ISavedJourneyRepository journeys, IEnumerable<SavedJourney> rows)
    {
        var ids = rows
            .SelectMany(j => j.Legs)
            .SelectMany(LegTaxiIds)
            .ToHashSet();
        return await journeys.LoadTaxisAsync(ids);
    }

    private static async Task<JourneyDto> BuildAsync(
        ISavedJourneyRepository journeys, IUnitOfWork unitOfWork, Guid userId, SavedJourney journey, HashSet<int> fareChanged,
        ILogger log, TripLinkTarget? target = null)
    {
        // The journey is already saved when this runs: a failed read of the trip rows only drops the booking status.
        IReadOnlyList<TripEnquiry>? segmentRows = null;
        try
        {
            segmentRows = await journeys.GetSegmentRowsAsync(journey.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "journey.build.segmentrows.failed {JourneyId}", journey.Id);
        }
        var dto = Map(journey, await LoadTaxisAsync(journeys, [journey]), fareChanged, segmentRows);
        var trips = target != null
            ? new Dictionary<Guid, (Trip Trip, TripPlan Plan)> { [target.Trip.Id] = (target.Trip, target.Plan) }
            : await LoadTripsAsync(unitOfWork, [journey], userId);
        return WithTrip(dto, journey, trips, target);
    }

    // One owner-scoped trips query per response.
    private static async Task<Dictionary<Guid, (Trip Trip, TripPlan Plan)>> LoadTripsAsync(
        IUnitOfWork unitOfWork, IEnumerable<SavedJourney> rows, Guid userId)
    {
        var ids = rows.Where(j => j.TripId != null).Select(j => j.TripId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, (Trip Trip, TripPlan Plan)>();
        var trips = await unitOfWork.Trips.GetByIdsForOwnerAsync(ids, userId);
        return trips.ToDictionary(t => t.Id, t => (t, TripPlanReader.Read(t)));
    }

    // A journey saved for a trip and not yet in its saved plan answers with the trip and role of the save request.
    private static JourneyDto WithTrip(
        JourneyDto dto, SavedJourney j, Dictionary<Guid, (Trip Trip, TripPlan Plan)> trips, TripLinkTarget? target)
    {
        if ((j.TripId ?? target?.Trip.Id) is not { } tripId || !trips.TryGetValue(tripId, out var entry)) return dto;
        dto.TripId = tripId;
        dto.TripTitle = entry.Trip.Title;
        var link = TripJourneyLinks.Resolve(entry.Plan, j.Id);
        if (link == null && target != null && target.Trip.Id == tripId)
            link = new TripJourneyLink(TripStepRoles.Segment, target.LegIndex, target.TravelDay);
        dto.TripRole = link?.Role;
        dto.TripLegIndex = link?.LegIndex;
        dto.TripTravelDay = link?.TravelDay;
        return dto;
    }

    // A car booked in a trip carries that row's status and agreed fare (tripRows: rows of the journey's trip or of the journey).
    private static TripEnquiry? BookedRow(IReadOnlyList<TripEnquiry>? tripRows, Guid journeyId, int legIndex)
        => tripRows?
            .Where(r => TripEnquiryStatuses.IsBooked(r.Status) && TripSegmentRows.JourneyIdOf(r) == journeyId
                        && TripSegmentRows.CarOf(r) == legIndex)
            .OrderByDescending(r => r.UpdatedAt)
            .FirstOrDefault();

    internal static JourneyDto Map(
        SavedJourney j, Dictionary<Guid, JourneyTaxiInfo> taxis, HashSet<int> fareChanged, IReadOnlyList<TripEnquiry>? tripRows = null) => new()
    {
        Id = j.Id,
        TripId = j.TripId,
        FromName = j.FromName,
        FromLat = (double)j.FromLatitude,
        FromLng = (double)j.FromLongitude,
        ToName = j.ToName,
        ToLat = (double)j.ToLatitude,
        ToLng = (double)j.ToLongitude,
        Date = j.TravelDate,
        People = j.People,
        PlanKey = j.PlanKey,
        Cars = j.Cars,
        Status = j.Status,
        ListedTotal = j.ListedTotal,
        CreatedAt = j.CreatedAt,
        UpdatedAt = j.UpdatedAt,
        LastRefreshedAt = j.LastRefreshedAt,
        Legs = j.Legs.OrderBy(l => l.LegIndex).Select(l => new JourneyLegDto
        {
            LegIndex = l.LegIndex,
            LegKey = l.LegKey,
            FromName = l.FromName,
            FromLat = (double)l.FromLatitude,
            FromLng = (double)l.FromLongitude,
            ToName = l.ToName,
            ToLat = (double)l.ToLatitude,
            ToLng = (double)l.ToLongitude,
            MeetingPlace = l.MeetingPlace,
            Driver = Driver(l.DriverTaxiId, taxis),
            Backups = new[] { l.Backup1TaxiId, l.Backup2TaxiId }
                .Where(b => b is { } g && taxis.TryGetValue(g, out var t) && t.IsPublic)
                .Select(b => Driver(b!.Value, taxis))
                .ToList(),
            ListedFare = l.ListedFare,
            CarsNeeded = JourneyLegPricing.CarsOfLeg(j.People, SeatsOf(l.DriverTaxiId, taxis), l.CoverTaxiIds),
            CoverTaxiIds = l.CoverTaxiIds.ToList(),
            CoverDrivers = l.CoverTaxiIds.Select(c => Driver(c, taxis)).ToList(),
            SeatsNote = l.CoverTaxiIds.Length > 0
                ? TaxiWaysRanking.CoverNote(j.People, l.CoverTaxiIds.Prepend(l.DriverTaxiId).Select(c => SeatsOf(c, taxis)))
                : taxis.TryGetValue(l.DriverTaxiId, out var legDriver) ? TaxiWaysRanking.SeatsNote(j.People, legDriver.Seats) : null,
            Partial = l.Partial,
            LastEnquiryId = l.LastEnquiryId,
            LastKnownStatus = l.LastKnownStatus,
            LastQuotedFare = l.LastQuotedFare,
            LastKnownAt = l.LastKnownAt,
            DriverAvailable = l.DriverAvailable,
            FareChanged = fareChanged.Contains(l.LegIndex),
            BookingStatus = BookedRow(tripRows, j.Id, l.LegIndex)?.Status,
            AgreedFare = BookedRow(tripRows, j.Id, l.LegIndex)?.AgreedFare,
        }).ToList(),
    };

    private static short? SeatsOf(Guid taxiId, Dictionary<Guid, JourneyTaxiInfo> taxis)
        => taxis.TryGetValue(taxiId, out var t) ? t.Seats : null;

    // Phone numbers only for a driver who is still listed.
    private static JourneyDriverDto Driver(Guid taxiId, Dictionary<Guid, JourneyTaxiInfo> taxis)
    {
        if (!taxis.TryGetValue(taxiId, out var t))
            return new JourneyDriverDto { TaxiId = taxiId, DriverName = "Driver", Available = false };
        return new JourneyDriverDto
        {
            TaxiId = taxiId,
            DriverName = t.DriverName,
            VehicleTypeName = t.VehicleTypeName,
            Seats = t.Seats,
            PlateDisplay = t.PlateDisplay,
            CityName = t.CityName,
            TripReplyLabel = t.TripReplyLabel,
            Phone = t.IsPublic ? t.Phone : null,
            WhatsApp = t.IsPublic ? t.WhatsApp : null,
            Available = t.IsPublic,
        };
    }
}
