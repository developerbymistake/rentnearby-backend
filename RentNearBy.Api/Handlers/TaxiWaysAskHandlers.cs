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
// A journey that is the travel between two cities of a trip writes segment rows instead (TripId set, Day = travel day).
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
        IRateLimitService rateLimiter, ITaxiWaysSearch ways, ILoggerFactory loggers, HttpContext http)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var journey = await journeys.GetOwnedAsync(request.JourneyId, callerId, tracked: true);
        if (journey == null) return JourneyHandlers.NotFound();
        if (journey.Status == JourneyStatuses.Completed) return BadRequestResponse("This journey is completed");
        if (request.Legs.Any(l => journey.Legs.All(x => x.LegIndex != l.LegIndex))) return BadRequestResponse("Unknown leg");

        // A journey in a trip: a step keeps the trip-less leads; travel between two cities writes segment rows on the trip.
        SegmentAsk? segment = null;
        if (journey.TripId is { } linkedTripId)
        {
            var (resolved, error) = await ResolveSegmentAsync(unitOfWork, journey, linkedTripId, callerId);
            if (error != null) return error;
            segment = resolved;
        }

        var now = DateTime.UtcNow;
        var date = segment?.Date ?? request.Date;
        var checkOut = date.AddDays(1);
        var travellerName = TripTextSafety.Clean(
            await db.Users.AsNoTracking().Where(u => u.Id == callerId).Select(u => u.Name).FirstOrDefaultAsync());
        var toName = TripTextSafety.Clean(request.ToName);

        var items = new List<(int LegIndex, TripEnquiry Enquiry)>();
        var skipped = new List<AskSkippedDto>();
        var candidates = new List<(AskLegRequest Leg, TaxiRow Taxi)>();
        // A people change makes a new journey for the same plan and date (D24): its open leads replace the old journey's.
        var siblingIds = await db.Set<SavedJourney>().AsNoTracking()
            .Where(j => j.UserId == callerId && !j.IsDeleted && j.Id != journey.Id
                && j.PlanKey == journey.PlanKey && j.TravelDate == journey.TravelDate)
            .Select(j => j.Id)
            .ToListAsync();
        var replacedLeadIds = new HashSet<Guid>();
        var openSegmentRows = segment == null
            ? new List<TripEnquiry>()
            : TripSegmentRows.OfJourney(
                await db.TripEnquiries.AsNoTracking()
                    .Where(x => x.TripId == segment.Trip.Id && x.Kind == TripEnquiryKinds.Taxi && x.Day != null
                        && TripEnquiryStatuses.Open.Contains(x.Status))
                    .ToListAsync(),
                journey.Id);

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
                if (segment != null)
                {
                    var pick = SegmentPick(segment, journey.Id, siblingIds, leg.LegIndex, taxiId, mine, openSegmentRows,
                        candidates.Select(c => (c.Leg.LegIndex, c.Taxi.Id)).ToList());
                    if (pick.Same != null) items.Add((leg.LegIndex, pick.Same));
                    else if (pick.Skip != null) skipped.Add(Skip(leg.LegIndex, taxiId, pick.Skip));
                    else
                    {
                        replacedLeadIds.UnionWith(pick.Replaced);
                        candidates.Add((leg, taxi));
                    }
                    continue;
                }
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
                foreach (var old in mine.Where(x => x.TripId == null && TripEnquiryStatuses.IsWithdrawable(x.Status)
                    && x.CheckIn is { } a && x.CheckOut is { } b && TripExecutionRules.NightsOverlap(a, b, date, checkOut)
                    && IsLeadOfAny(x, siblingIds, leg.LegIndex)))
                    replacedLeadIds.Add(old.Id);
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
        var leadPrices = await LeadPricesAsync(ways, journeys, journey, candidates.Select(c => (c.Leg.LegIndex, c.Taxi.Id, c.Taxi.Seats)).ToList(), request.People);

        await unitOfWork.BeginTransactionAsync();
        try
        {
            if (replacedLeadIds.Count > 0)
            {
                var ids = replacedLeadIds.ToList();
                var oldLeads = await db.TripEnquiries.Where(x => ids.Contains(x.Id)).ToListAsync();
                foreach (var old in oldLeads.Where(x => TripEnquiryStatuses.IsWithdrawable(x.Status)))
                {
                    old.Status = TripEnquiryStatuses.Closed;
                    old.FailReason = TripCloseReasons.Withdrawn;
                    old.UpdatedAt = now;
                    pushes.Add(new TripPendingPush(old, old.OwnerUserId, TripEnquiryHandlers.EnquiryWithdrawn));
                }
                await db.SaveChangesAsync();
            }

            foreach (var (leg, taxi) in candidates)
            {
                var fromName = TripTextSafety.Clean(leg.FromName);
                var legTo = TripTextSafety.Clean(leg.ToName);
                var pickup = JourneyLeadText.PickupPlace(fromName, leg.LegIndex, leg.CarOf);
                var comesFrom = string.IsNullOrWhiteSpace(leg.ComesFrom) ? null : TripTextSafety.Clean(leg.ComesFrom);
                var price = leadPrices[(leg.LegIndex, taxi.Id)];
                if (segment != null)
                {
                    var (row, created) = await InsertSegmentRowAsync(
                        unitOfWork, db, segment, journey, leg, taxi, fromName, legTo, comesFrom, request.People,
                        travellerName.Length == 0 ? null : travellerName, expiresAt, isUrgent, now, price);
                    if (row == null)
                    {
                        skipped.Add(Skip(leg.LegIndex, taxi.Id, TripSkipReasons.AlreadyOpen));
                        continue;
                    }
                    items.Add((leg.LegIndex, row));
                    if (created) pushes.Add(new TripPendingPush(row, row.OwnerUserId, TripNotifications.TripLeadReceived));
                    continue;
                }
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
                        fromName, legTo, date, request.People, leg.LegIndex, leg.CarOf, comesFrom, price.Cars),
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
                    ListedRateSnapshot = price.Fare,
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

    internal sealed record SegmentAsk(Trip Trip, TripPlan Plan, TripCityLeg Leg, int TravelDay, DateOnly Date);

    internal sealed record SegmentPickResult(TripEnquiry? Same, string? Skip, IReadOnlyList<Guid> Replaced);

    // Null segment = a step journey or a journey no plan field references: today's trip-less path.
    private static async Task<(SegmentAsk? Segment, IResult? Error)> ResolveSegmentAsync(
        IUnitOfWork unitOfWork, SavedJourney journey, Guid tripId, Guid callerId)
    {
        var trip = (await unitOfWork.Trips.GetByIdsForOwnerAsync([tripId], callerId)).FirstOrDefault();
        if (trip == null) return (null, ErrorResponse(404, "Trip not found", TripErrorTypes.TripNotFound));
        var plan = TripPlanReader.Read(trip);
        var link = TripJourneyLinks.Resolve(plan, journey.Id);
        // The plan save writes the plan and TripId together, so an unreferenced TripId is a stale link: it is cleared here.
        if (link == null)
        {
            journey.TripId = null;
            return (null, null);
        }
        if (link.Role != TripStepRoles.Segment) return (null, null);

        var leg = plan.Legs?.FirstOrDefault(l => l != null && l.Index == link.LegIndex);
        if (leg == null || link.TravelDay is not { } travelDay || travelDay <= 0) return (null, BadRequestResponse("Unknown city"));
        if (trip.StartDate is not { } start)
            return (null, ErrorResponse(422, "Add your trip dates first.", TripEnquiryErrorTypes.TripDatesRequired));
        var date = TripExecutionRules.NightDate(start, travelDay);
        if (journey.TravelDate != date)
            return (null, ErrorResponse(409, "Your trip date changed. Search again.", TripEnquiryErrorTypes.TravelDateChanged));
        if (trip.Status == TripStatuses.Completed) return (null, BadRequestResponse("This trip is completed"));
        return (new SegmentAsk(trip, plan, leg, travelDay, date), null);
    }

    // Dedupe inside the journey only: a local lead of the trip (Day null) is never adopted or replaced.
    // journeyRows = open segment rows of this journey; pending = (car, taxi) already chosen in this request.
    internal static SegmentPickResult SegmentPick(
        SegmentAsk segment, Guid journeyId, IReadOnlyCollection<Guid> siblingIds, int car, Guid taxiId,
        IEnumerable<TripEnquiry> mine, IReadOnlyList<TripEnquiry> journeyRows, IReadOnlyList<(int Car, Guid TaxiId)> pending)
    {
        var day = (short)segment.TravelDay;
        var checkOut = segment.Date.AddDays(1);
        var rows = mine.ToList();
        var same = rows.FirstOrDefault(x => x.TripId == segment.Trip.Id && x.Day == day && IsLeadOf(x, journeyId, car));
        if (same != null) return new SegmentPickResult(same, null, []);
        // One open row per trip, taxi and travel day: this driver already carries another car of this journey.
        if (pending.Any(p => p.TaxiId == taxiId)) return new SegmentPickResult(null, TripSkipReasons.AlreadyOpen, []);

        var overlapping = rows.Where(x => x.CheckIn is { } a && x.CheckOut is { } b
            && TripExecutionRules.NightsOverlap(a, b, segment.Date, checkOut)).ToList();
        // This journey's own trip-less lead (asked before it joined the trip) and a sibling's lead give way to the segment row.
        var earlier = overlapping
            .Where(x => (x.TripId == null && IsLeadOf(x, journeyId, car))
                || ((x.TripId == null || x.TripId == segment.Trip.Id) && IsLeadOfAny(x, siblingIds, car)))
            .ToList();
        if (earlier.Any(x => !TripEnquiryStatuses.IsWithdrawable(x.Status)))
            return new SegmentPickResult(null, TripSkipReasons.AlreadyBooked, []);
        var replaced = earlier.Select(x => x.Id).ToList();
        // The driver's row of a journey the plan no longer holds gives way; another car's row keeps the day (skip reported).
        foreach (var stale in rows.Where(x => x.TripId == segment.Trip.Id && x.Day == day && !replaced.Contains(x.Id)
            && TripSegmentRows.Classify(x, segment.Plan, journeyId, car) == TripSegmentRows.RowStale))
        {
            if (!TripEnquiryStatuses.IsWithdrawable(stale.Status)) return new SegmentPickResult(null, TripSkipReasons.AlreadyBooked, []);
            replaced.Add(stale.Id);
        }
        if (rows.Any(x => x.TripId == segment.Trip.Id && x.Day == day && !replaced.Contains(x.Id)))
            return new SegmentPickResult(null, TripSkipReasons.AlreadyOpen, []);

        var open = journeyRows.Count(r => TripSegmentRows.CarOf(r) == car) + pending.Count(p => p.Car == car);
        if (open >= JourneyRules.MaxAskPerLeg) return new SegmentPickResult(null, TripSkipReasons.LimitReached, []);
        return new SegmentPickResult(null, null, replaced);
    }

    // Created false: a parallel ask won (trip, taxi, travel day); the winner is returned only when it is this car's lead.
    private static async Task<(TripEnquiry? Row, bool Created)> InsertSegmentRowAsync(
        IUnitOfWork unitOfWork, ApplicationDbContext db, SegmentAsk segment, SavedJourney journey, AskLegRequest leg, TaxiRow taxi,
        string fromName, string toName, string? comesFrom, int people, string? travellerName, DateTime expiresAt, bool isUrgent,
        DateTime now, LeadPrice price)
    {
        var day = (short)segment.TravelDay;
        var context = TripSegmentContext.Build(
            segment.Trip, segment.Plan, segment.Leg, segment.TravelDay, segment.Date, journey.Id, leg.LegIndex, leg.CarOf,
            fromName, toName, comesFrom, people, price.Cars, travellerName, null, null);
        var e = new TripEnquiry
        {
            Id = Guid.NewGuid(),
            TripId = segment.Trip.Id,
            TravellerUserId = journey.UserId,
            OwnerUserId = taxi.UserId,
            Kind = TripEnquiryKinds.Taxi,
            ListingId = taxi.Id,
            ListingName = TaxiName(taxi),
            ListingLatitude = taxi.Latitude,
            ListingLongitude = taxi.Longitude,
            Day = day,
            DaysJson = $"[{segment.TravelDay}]",
            CheckIn = segment.Date,
            CheckOut = segment.Date.AddDays(1),
            ConversationId = null,
            ContextJson = JsonSerializer.Serialize(context, TripJson.Options),
            Status = TripEnquiryStatuses.Asked,
            ExpiresAt = expiresAt,
            IsUrgent = isUrgent,
            AdvanceStatus = TripAdvanceStatuses.None,
            ListedRateSnapshot = price.Fare,
            PickupTime = null,
            PickupPlace = context.PickupPlace,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.TripEnquiries.Add(e);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (TripEnquiryHandlers.IsOpenUniqueViolation(ex))
        {
            db.Entry(e).State = EntityState.Detached;
            var winner = await unitOfWork.TripEnquiries.FindOpenAsync(segment.Trip.Id, TripEnquiryKinds.Taxi, taxi.Id, day);
            return (winner != null && IsLeadOf(winner, journey.Id, leg.LegIndex) ? winner : null, false);
        }

        await db.TaxiListings.Where(t => t.Id == taxi.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TripLeadsReceived, x => x.TripLeadsReceived + 1));
        return (e, true);
    }

    // Cars this driver brings and his listed rate for them; Fare null when the server cannot price him on the stretch.
    internal readonly record struct LeadPrice(int Cars, int? Fare);

    // Per (leg, taxi) of the new leads: cars from the saved leg's seat cover (JourneyLegPricing.CarsFor) and the server fare of
    // the stretch (JourneyLegFares). The app's listedFare is never used.
    private static async Task<Dictionary<(int LegIndex, Guid TaxiId), LeadPrice>> LeadPricesAsync(
        ITaxiWaysSearch ways, ISavedJourneyRepository journeys, SavedJourney journey,
        IReadOnlyList<(int LegIndex, Guid TaxiId, short? Seats)> asks, int people)
    {
        var prices = new Dictionary<(int, Guid), LeadPrice>();
        if (asks.Count == 0) return prices;
        var noTaxis = new Dictionary<Guid, JourneyTaxiInfo>();
        foreach (var group in asks.GroupBy(a => a.LegIndex))
        {
            var leg = journey.Legs.First(l => l.LegIndex == group.Key);
            var previousRoute = journey.Legs.FirstOrDefault(l => l.LegIndex == group.Key - 1)?.DriverRouteId;
            var fares = await JourneyLegFares.LoadAsync(ways, journeys, leg, group.Select(a => a.TaxiId), noTaxis, previousRoute);
            foreach (var (legIndex, taxiId, seats) in group)
            {
                var cars = JourneyLegPricing.CarsFor(taxiId, seats, people, leg.DriverTaxiId, leg.CoverTaxiIds);
                prices[(legIndex, taxiId)] = new LeadPrice(
                    cars, fares.TryGetValue(taxiId, out var f) ? JourneyLegPricing.LeadFare(f.PerCarFare, cars) : null);
            }
        }
        return prices;
    }

    private static bool IsLeadOf(TripEnquiry e, Guid journeyId, int legIndex)
    {
        var context = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
        return context.JourneyId == journeyId && context.LegIndex == legIndex;
    }

    private static bool IsLeadOfAny(TripEnquiry e, IReadOnlyCollection<Guid> journeyIds, int legIndex)
    {
        if (journeyIds.Count == 0) return false;
        var context = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
        return context.JourneyId is { } id && journeyIds.Contains(id) && context.LegIndex == legIndex;
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
