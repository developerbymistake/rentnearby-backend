using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Saved journeys of /api/v1/taxis/journeys. Owner-only rows (a foreign id answers 404), logged-in callers only.
public static class JourneyHandlers
{
    private static readonly TimeSpan SaveWindow = TimeSpan.FromMinutes(10);
    private const int SavePerWindow = 20;

    private static IResult Invalid(FluentValidation.Results.ValidationResult v) => BadRequestResponse(v.Errors[0].ErrorMessage);

    internal static IResult NotFound()
        => ErrorResponse(404, "Journey not found", JourneyErrorTypes.JourneyNotFound);

    private static IResult Concurrent()
        => ConflictResponse("This journey just changed.", JourneyErrorTypes.ConcurrentUpdate);

    // ---------- POST /taxis/journeys ----------

    public static async Task<IResult> Save(
        SaveJourneyRequest request, IValidator<SaveJourneyRequest> validator, ClaimsPrincipal principal,
        ISavedJourneyRepository journeys, IRateLimitService rateLimiter, HttpContext http)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var limited = await rateLimiter.CheckAsync($"taxi:journey:save:{userId}", SavePerWindow, SaveWindow);
        if (!limited.IsAllowed)
        {
            http.Response.Headers["Retry-After"] = ((int)(limited.RetryAfter ?? SaveWindow).TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var existing = await journeys.FindByPlanAsync(userId, request.PlanKey, request.Date);
        if (existing != null) return OkResponse(await BuildAsync(journeys, existing, new HashSet<int>()));

        if (await journeys.CountLiveAsync(userId) >= JourneyRules.MaxLive)
            return ConflictResponse("Up to 20 saved journeys.", JourneyErrorTypes.JourneyLimitReached);

        var legs = request.Legs.OrderBy(l => l.LegIndex).ToList();
        var ids = legs
            .SelectMany(l => new[] { l.DriverTaxiId, l.Backup1TaxiId ?? Guid.Empty, l.Backup2TaxiId ?? Guid.Empty })
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var taxis = await journeys.LoadTaxisAsync(ids);

        var now = DateTime.UtcNow;
        var journey = new SavedJourney
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FromName = TripTextSafety.Clean(request.FromName),
            FromLatitude = (decimal)request.FromLat,
            FromLongitude = (decimal)request.FromLng,
            ToName = TripTextSafety.Clean(request.ToName),
            ToLatitude = (decimal)request.ToLat,
            ToLongitude = (decimal)request.ToLng,
            TravelDate = request.Date,
            People = (short)request.People,
            PlanKey = request.PlanKey,
            Cars = (short)legs.Count,
            CreatedAt = now,
            UpdatedAt = now,
        };

        foreach (var l in legs)
        {
            if (!taxis.TryGetValue(l.DriverTaxiId, out var driver) || !driver.IsPublic || driver.UserId == userId)
                return ErrorResponse(409, "A driver is not available any more.", JourneyErrorTypes.DriverNotOnLeg);

            var backups = new[] { l.Backup1TaxiId, l.Backup2TaxiId }
                .Where(b => b is { } g && g != l.DriverTaxiId && taxis.TryGetValue(g, out var t) && t.IsPublic && t.UserId != userId)
                .Select(b => b!.Value)
                .Distinct()
                .ToList();

            journey.Legs.Add(new SavedJourneyLeg
            {
                Id = Guid.NewGuid(),
                JourneyId = journey.Id,
                LegIndex = (short)l.LegIndex,
                LegKey = l.LegKey,
                FromName = TripTextSafety.Clean(l.FromName),
                FromLatitude = (decimal)l.FromLat,
                FromLongitude = (decimal)l.FromLng,
                ToName = TripTextSafety.Clean(l.ToName),
                ToLatitude = (decimal)l.ToLat,
                ToLongitude = (decimal)l.ToLng,
                MeetingPlace = string.IsNullOrWhiteSpace(l.MeetingPlace) ? null : TripTextSafety.Clean(l.MeetingPlace),
                DriverTaxiId = l.DriverTaxiId,
                DriverRouteId = l.DriverRouteId,
                // The request carries the driver's fare per car; the saved leg holds the whole stretch.
                ListedFare = l.ListedFare is { } perCar ? perCar * TaxiWaysRanking.CarsNeeded(request.People, driver.Seats) : null,
                Partial = l.Partial,
                Backup1TaxiId = backups.Count > 0 ? backups[0] : null,
                Backup2TaxiId = backups.Count > 1 ? backups[1] : null,
                LastKnownStatus = JourneyLegStatuses.Unknown,
                DriverAvailable = true,
                UpdatedAt = now,
            });
        }

        journey.ListedTotal = Total(journey.Legs);
        journey.Status = JourneyRules.JourneyStatusOf(journey.Legs);

        await journeys.AddAsync(journey);
        await journeys.SaveChangesAsync();
        return CreatedResponse(await BuildAsync(journeys, journey, new HashSet<int>()), $"/api/v1/taxis/journeys/{journey.Id}");
    }

    // ---------- GET /taxis/journeys ----------

    public static async Task<IResult> List(ClaimsPrincipal principal, ISavedJourneyRepository journeys)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var rows = await journeys.GetLiveByUserAsync(userId);
        var taxis = await LoadTaxisAsync(journeys, rows);
        return OkResponse(new JourneyListDto { Items = rows.Select(j => Map(j, taxis, new HashSet<int>())).ToList() });
    }

    // ---------- GET /taxis/journeys/{id} ----------

    public static async Task<IResult> Get(Guid id, ClaimsPrincipal principal, ISavedJourneyRepository journeys)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var journey = await journeys.GetOwnedAsync(id, userId, tracked: false);
        if (journey == null) return NotFound();
        return OkResponse(await BuildAsync(journeys, journey, new HashSet<int>()));
    }

    // ---------- DELETE /taxis/journeys/{id} ----------

    public static async Task<IResult> Delete(Guid id, ClaimsPrincipal principal, ISavedJourneyRepository journeys)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var journey = await journeys.GetOwnedAsync(id, userId, tracked: true);
        if (journey == null) return NotFound();
        journey.IsDeleted = true;
        journey.UpdatedAt = DateTime.UtcNow;
        try
        {
            await journeys.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Concurrent();
        }
        return NoContentResponse();
    }

    // ---------- POST /taxis/journeys/{id}/refresh ----------

    public static async Task<IResult> Refresh(
        Guid id, ClaimsPrincipal principal, ISavedJourneyRepository journeys, IRateLimitService rateLimiter, HttpContext http)
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
                .SelectMany(l => new[] { l.DriverTaxiId, l.Backup1TaxiId ?? Guid.Empty, l.Backup2TaxiId ?? Guid.Empty })
                .Where(g => g != Guid.Empty)
                .ToHashSet();
            var taxis = await journeys.LoadTaxisAsync(taxiIds);
            var enquiries = await journeys.LoadEnquiriesAsync(journey.Legs.Where(l => l.LastEnquiryId != null).Select(l => l.LastEnquiryId!.Value).ToList());

            foreach (var leg in journey.Legs.OrderBy(l => l.LegIndex))
            {
                await RefreshLegAsync(journeys, journey, leg, taxis, enquiries, fareChanged, now, journey.People);
                leg.UpdatedAt = now;
            }

            journey.ListedTotal = Total(journey.Legs);
            journey.Status = JourneyRules.JourneyStatusOf(journey.Legs);
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
        return OkResponse(await BuildAsync(journeys, journey, fareChanged));
    }

    private static async Task RefreshLegAsync(
        ISavedJourneyRepository journeys, SavedJourney journey, SavedJourneyLeg leg, Dictionary<Guid, JourneyTaxiInfo> taxis,
        Dictionary<Guid, TripEnquiry> enquiries, HashSet<int> fareChanged, DateTime now, int people)
    {
        var driverLive = taxis.TryGetValue(leg.DriverTaxiId, out var driver) && driver.IsPublic;
        if (driverLive)
        {
            var match = await journeys.FindOnLegAsync(
                leg.DriverTaxiId, (double)leg.FromLatitude, (double)leg.FromLongitude, (double)leg.ToLatitude, (double)leg.ToLongitude);
            if (match != null)
            {
                var fare = match.Fare * TaxiWaysRanking.CarsNeeded(people, driver!.Seats);
                if (leg.ListedFare != fare)
                {
                    if (leg.ListedFare != null) fareChanged.Add(leg.LegIndex);
                    leg.ListedFare = fare;
                }
                leg.DriverRouteId = match.RouteId;
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
        {
            var status = JourneyRules.LegStatusOf(e);
            leg.LastKnownStatus = status;
            leg.LastQuotedFare = JourneyRules.QuotedFareOf(e);
            leg.LastKnownAt = status == JourneyLegStatuses.NoAnswer ? e.CreatedAt : e.DriverRepliedAt ?? e.UpdatedAt;
        }
    }

    // ---------- PUT /taxis/journeys/{id}/legs/{legIndex}/driver ----------

    public static async Task<IResult> ReplaceDriver(
        Guid id, int legIndex, ReplaceJourneyDriverRequest request, IValidator<ReplaceJourneyDriverRequest> validator,
        ClaimsPrincipal principal, ISavedJourneyRepository journeys)
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
            var taxis = await journeys.LoadTaxisAsync([request.TaxiId, leg.DriverTaxiId]);
            if (!taxis.TryGetValue(request.TaxiId, out var next) || !next.IsPublic || next.UserId == userId)
                return ErrorResponse(409, "This driver is not available.", JourneyErrorTypes.DriverNotOnLeg);

            var match = await journeys.FindOnLegAsync(
                request.TaxiId, (double)leg.FromLatitude, (double)leg.FromLongitude, (double)leg.ToLatitude, (double)leg.ToLongitude);
            if (match == null)
                return ErrorResponse(409, "This driver does not go on this stretch.", JourneyErrorTypes.DriverNotOnLeg);

            var oldId = leg.DriverTaxiId;
            var oldLive = taxis.TryGetValue(oldId, out var old) && old.IsPublic;
            var backups = new List<Guid>();
            if (oldLive) backups.Add(oldId);
            backups.AddRange(new[] { leg.Backup1TaxiId, leg.Backup2TaxiId }.Where(b => b is { } g && g != request.TaxiId).Select(b => b!.Value));

            var now = DateTime.UtcNow;
            leg.DriverTaxiId = request.TaxiId;
            leg.DriverRouteId = match.RouteId;
            leg.ListedFare = match.Fare * TaxiWaysRanking.CarsNeeded(journey.People, next.Seats);
            leg.Backup1TaxiId = backups.Count > 0 ? backups[0] : null;
            leg.Backup2TaxiId = backups.Count > 1 ? backups[1] : null;
            leg.LastEnquiryId = null;
            leg.LastKnownStatus = JourneyLegStatuses.Unknown;
            leg.LastQuotedFare = null;
            leg.LastKnownAt = null;
            leg.DriverAvailable = true;
            leg.UpdatedAt = now;

            journey.ListedTotal = Total(journey.Legs);
            journey.Status = JourneyRules.JourneyStatusOf(journey.Legs);
            journey.UpdatedAt = now;
            try
            {
                await journeys.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Concurrent();
            }
        }
        return OkResponse(await BuildAsync(journeys, journey, new HashSet<int>()));
    }

    // ---------- PUT /taxis/journeys/{id}/status ----------

    public static async Task<IResult> SetStatus(
        Guid id, JourneyStatusRequest request, IValidator<JourneyStatusRequest> validator, ClaimsPrincipal principal,
        ISavedJourneyRepository journeys)
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
        return OkResponse(await BuildAsync(journeys, journey, new HashSet<int>()));
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
            .SelectMany(l => new[] { l.DriverTaxiId, l.Backup1TaxiId ?? Guid.Empty, l.Backup2TaxiId ?? Guid.Empty })
            .Where(g => g != Guid.Empty)
            .ToHashSet();
        return await journeys.LoadTaxisAsync(ids);
    }

    private static async Task<JourneyDto> BuildAsync(ISavedJourneyRepository journeys, SavedJourney journey, HashSet<int> fareChanged)
        => Map(journey, await LoadTaxisAsync(journeys, [journey]), fareChanged);

    internal static JourneyDto Map(SavedJourney j, Dictionary<Guid, JourneyTaxiInfo> taxis, HashSet<int> fareChanged) => new()
    {
        Id = j.Id,
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
            CarsNeeded = CarsOf(j.People, l.DriverTaxiId, taxis),
            SeatsNote = taxis.TryGetValue(l.DriverTaxiId, out var legDriver) ? TaxiWaysRanking.SeatsNote(j.People, legDriver.Seats) : null,
            Partial = l.Partial,
            LastEnquiryId = l.LastEnquiryId,
            LastKnownStatus = l.LastKnownStatus,
            LastQuotedFare = l.LastQuotedFare,
            LastKnownAt = l.LastKnownAt,
            DriverAvailable = l.DriverAvailable,
            FareChanged = fareChanged.Contains(l.LegIndex),
        }).ToList(),
    };

    // Phone numbers only for a driver who is still listed.
    private static int CarsOf(int people, Guid taxiId, Dictionary<Guid, JourneyTaxiInfo> taxis)
        => TaxiWaysRanking.CarsNeeded(people, taxis.TryGetValue(taxiId, out var t) ? t.Seats : null);

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
