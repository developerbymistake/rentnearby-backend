using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// POST /taxis/ways/ask: trip-less taxi leads (TripId null) that belong to a saved journey. The driver answers them
// in /trip-leads through the untouched TripLeadHandlers; the journey screen shows the answer on Refresh.
public static class TaxiWaysAskHandlers
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day24 = TimeSpan.FromHours(24);
    private const string AsksLeftHeader = "X-Trip-Asks-Left";

    private sealed record TaxiRow(
        Guid Id, Guid UserId, string? DriverName, string? VehicleTypeName, string Status, bool IsAvailable, bool IsDeleted,
        double? Latitude, double? Longitude, short? Seats);

    public static async Task<IResult> AskDrivers(
        AskDriversRequest request, IValidator<AskDriversRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ISavedJourneyRepository journeys, ApplicationDbContext db, ITripNotifier notifier,
        IRateLimitService rateLimiter, ILoggerFactory loggers, HttpContext http)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var journey = await journeys.GetOwnedAsync(request.JourneyId, callerId, tracked: true);
        if (journey == null) return JourneyHandlers.NotFound();
        if (journey.Status == JourneyStatuses.Completed) return BadRequestResponse("This journey is completed");
        if (request.Legs.Any(l => journey.Legs.All(x => x.LegIndex != l.LegIndex))) return BadRequestResponse("Unknown leg");

        var now = DateTime.UtcNow;
        var date = request.Date;
        var checkOut = date.AddDays(1);
        var travellerName = TripTextSafety.Clean(
            await db.Users.AsNoTracking().Where(u => u.Id == callerId).Select(u => u.Name).FirstOrDefaultAsync());
        var toName = TripTextSafety.Clean(request.ToName);

        var items = new List<(int LegIndex, TripEnquiry Enquiry)>();
        var skipped = new List<AskSkippedDto>();
        var candidates = new List<(AskLegRequest Leg, TaxiRow Taxi)>();

        foreach (var leg in request.Legs.OrderBy(l => l.LegIndex))
        {
            foreach (var taxiId in leg.TaxiIds)
            {
                // One lead per leg and driver: the same driver on two legs gets a lead for each, with that leg's route.
                if (candidates.Any(c => c.Leg.LegIndex == leg.LegIndex && c.Taxi.Id == taxiId)
                    || items.Any(i => i.LegIndex == leg.LegIndex && i.Enquiry.ListingId == taxiId)) continue;
                var taxi = await LoadTaxiAsync(db, taxiId);
                if (taxi == null || taxi.IsDeleted || taxi.Status != TaxiStatuses.Approved || !taxi.IsAvailable || taxi.UserId == callerId)
                {
                    skipped.Add(Skip(leg.LegIndex, taxiId, TripSkipReasons.NotAvailable));
                    continue;
                }
                if (await unitOfWork.UserBlocks.ExistsAsync(callerId, taxi.UserId) || await unitOfWork.UserBlocks.ExistsAsync(taxi.UserId, callerId))
                {
                    skipped.Add(Skip(leg.LegIndex, taxiId, TripSkipReasons.Blocked));
                    continue;
                }

                var mine = await unitOfWork.TripEnquiries.GetOpenByTravellerListingAsync(callerId, TripEnquiryKinds.Taxi, taxiId);
                // A trip's local-taxi lead (TripId set) is never this journey's lead: the same driver can be both.
                // Another journey's or another leg's lead is a different route, so only this journey and leg are reused.
                var same = mine.FirstOrDefault(x => x.TripId == null && x.CheckIn is { } a && x.CheckOut is { } b
                    && TripExecutionRules.NightsOverlap(a, b, date, checkOut)
                    && IsLeadOf(x, journey.Id, leg.LegIndex));
                if (same != null)
                {
                    items.Add((leg.LegIndex, same));
                    continue;
                }
                candidates.Add((leg, taxi));
            }
        }

        // Only an ask that sends a new lead spends the shared ask quota.
        if (candidates.Count > 0)
        {
            var hourlyCheck = await rateLimiter.CheckAsync($"trip:enquiry:h:{callerId}", TripExecutionRules.EnquiriesPerHour, Hour);
            http.Response.Headers[AsksLeftHeader] = Math.Max(0, hourlyCheck.RemainingAttempts).ToString();
            if (!hourlyCheck.IsAllowed) return Limited(http, hourlyCheck, Hour);
            if (await RateLimitedAsync(rateLimiter, http, $"trip:enquiry:d:{callerId}", TripExecutionRules.EnquiriesPerDay, Day24) is { } daily)
                return daily;
        }

        var (expiresAt, veryShortNotice) = TripExecutionRules.EnquiryExpiry(date, date, now);
        var isUrgent = TripExecutionRules.IsUrgent(date, now);
        var pushes = new List<TripPendingPush>();

        await unitOfWork.BeginTransactionAsync();
        try
        {
            foreach (var (leg, taxi) in candidates)
            {
                var fromName = TripTextSafety.Clean(leg.FromName);
                var legTo = TripTextSafety.Clean(leg.ToName);
                var pickup = JourneyLeadText.PickupPlace(fromName, leg.LegIndex, leg.CarOf);
                var context = new TripEnquiryContext
                {
                    City = toName,
                    Adults = request.People,
                    Kids = 0,
                    Rooms = null,
                    Needs = [],
                    TravellerName = travellerName.Length == 0 ? null : travellerName,
                    Days = [new TripEnquiryContextDay { Day = 1, Date = date, Minutes = 0, Spots = [] }],
                    PickupTime = null,
                    PickupPlace = pickup,
                    Text = JourneyLeadText.Build(
                        fromName, legTo, date, request.People, leg.LegIndex, leg.CarOf,
                        string.IsNullOrWhiteSpace(leg.ComesFrom) ? null : TripTextSafety.Clean(leg.ComesFrom),
                        TaxiWaysRanking.CarsNeeded(request.People, taxi.Seats)),
                    JourneyId = journey.Id,
                    LegIndex = leg.LegIndex,
                };

                var e = new TripEnquiry
                {
                    Id = Guid.NewGuid(),
                    TripId = null,
                    TravellerUserId = callerId,
                    OwnerUserId = taxi.UserId,
                    Kind = TripEnquiryKinds.Taxi,
                    ListingId = taxi.Id,
                    ListingName = TaxiName(taxi),
                    ListingLatitude = taxi.Latitude,
                    ListingLongitude = taxi.Longitude,
                    Day = null,
                    DaysJson = "[1]",
                    CheckIn = date,
                    CheckOut = checkOut,
                    ConversationId = null,
                    ContextJson = JsonSerializer.Serialize(context, TripJson.Options),
                    Status = TripEnquiryStatuses.Asked,
                    ExpiresAt = expiresAt,
                    IsUrgent = isUrgent,
                    AdvanceStatus = TripAdvanceStatuses.None,
                    ListedRateSnapshot = leg.ListedFare,
                    PickupTime = null,
                    PickupPlace = pickup,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.TripEnquiries.Add(e);
                await db.SaveChangesAsync();

                await db.TaxiListings.Where(t => t.Id == taxi.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.TripLeadsReceived, x => x.TripLeadsReceived + 1));

                items.Add((leg.LegIndex, e));
                pushes.Add(new TripPendingPush(e, e.OwnerUserId, TripNotifications.TripLeadReceived));
            }

            // The chosen driver's lead is the leg's current answer; the others are backups' leads.
            foreach (var jl in journey.Legs)
            {
                var row = items.FirstOrDefault(i => i.LegIndex == jl.LegIndex && i.Enquiry.ListingId == jl.DriverTaxiId).Enquiry;
                if (row == null) continue;
                jl.LastEnquiryId = row.Id;
                jl.LastKnownStatus = JourneyRules.LegStatusOf(row);
                jl.LastQuotedFare = JourneyRules.QuotedFareOf(row);
                jl.LastKnownAt = row.CreatedAt;
                jl.UpdatedAt = now;
            }
            journey.Status = JourneyRules.JourneyStatusOf(journey.Legs);
            journey.UpdatedAt = now;
            await db.SaveChangesAsync();

            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return ConflictResponse("This journey just changed.", JourneyErrorTypes.ConcurrentUpdate);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await TripPushSender.SendAsync(pushes, unitOfWork.TripEnquiries, notifier, loggers.CreateLogger("TaxiWaysAsk"));

        return CreatedResponse(new AskDriversResultDto
        {
            Items = items.Select(i => new AskItemDto
            {
                LegIndex = i.LegIndex,
                TaxiId = i.Enquiry.ListingId,
                EnquiryId = i.Enquiry.Id,
                Status = i.Enquiry.Status,
            }).ToList(),
            Skipped = skipped,
            VeryShortNotice = veryShortNotice,
        }, $"/api/v1/taxis/journeys/{journey.Id}");
    }

    private static bool IsLeadOf(TripEnquiry e, Guid journeyId, int legIndex)
    {
        var context = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
        return context.JourneyId == journeyId && context.LegIndex == legIndex;
    }

    private static AskSkippedDto Skip(int legIndex, Guid taxiId, string reason)
        => new() { LegIndex = legIndex, TaxiId = taxiId, Reason = reason };

    private static async Task<TaxiRow?> LoadTaxiAsync(ApplicationDbContext db, Guid taxiId)
        => await db.TaxiListings.AsNoTracking()
            .Where(t => t.Id == taxiId)
            .Select(t => new TaxiRow(
                t.Id, t.UserId, t.User.Name, t.VehicleType != null ? t.VehicleType.Name : null, t.Status, t.IsAvailable, t.IsDeleted,
                t.Latitude == null ? null : (double?)t.Latitude, t.Longitude == null ? null : (double?)t.Longitude, t.Seats))
            .FirstOrDefaultAsync();

    // "Driver · Vehicle", at most 120 characters.
    private static string TaxiName(TaxiRow taxi)
    {
        var driver = TripTextSafety.Clean(taxi.DriverName, 120);
        var vehicle = TripTextSafety.Clean(taxi.VehicleTypeName, 120);
        var name = driver.Length > 0 && vehicle.Length > 0 ? $"{driver} · {vehicle}" : driver.Length > 0 ? driver : vehicle;
        if (name.Length == 0) return "Taxi";
        return name.Length <= 120 ? name : name[..120].TrimEnd();
    }

    private static async Task<IResult?> RateLimitedAsync(IRateLimitService rateLimiter, HttpContext http, string key, int max, TimeSpan window)
    {
        var rl = await rateLimiter.CheckAsync(key, max, window);
        return rl.IsAllowed ? null : Limited(http, rl, window);
    }

    private static IResult Limited(HttpContext http, RateLimitResult rl, TimeSpan window)
    {
        http.Response.Headers["Retry-After"] = ((int)(rl.RetryAfter ?? window).TotalSeconds).ToString();
        return TooManyRequestsResponse();
    }
}
