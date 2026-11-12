using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Validators;
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
// A journey a trip uses (Getting there, Coming back, travel between two cities) writes journey rows on the trip instead
// (TripId set, Day = travel day, dated by TripStepDates).
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
        IRateLimitService rateLimiter, ITaxiWaysSearch ways, ILoggerFactory loggers, HttpContext http, IOutboxWriter outbox)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return validation.Errors[0].ErrorMessage == TaxiWaysRules.DateMessage
                ? BadRequestResponse(TaxiWaysRules.DateMessage, TaxiWaysErrorTypes.InvalidDate)
                : BadRequestResponse(validation.Errors[0].ErrorMessage);

        var journey = await journeys.GetOwnedAsync(request.JourneyId, callerId, tracked: true);
        if (journey == null) return JourneyHandlers.NotFound();
        if (journey.Status == JourneyStatuses.Completed) return BadRequestResponse("This journey is completed");
        if (request.Legs.Any(l => journey.Legs.All(x => x.LegIndex != l.LegIndex))) return BadRequestResponse("Unknown leg");

        // A journey in a trip writes journey rows on the trip (every role).
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
        var lastLeg = journey.Legs.OrderBy(l => l.LegIndex).LastOrDefault();
        var toName = TaxiWaysLabels.Of(
            string.IsNullOrWhiteSpace(request.ToName) ? lastLeg?.ToName : request.ToName, null, false, true);

        var items = new List<(int LegIndex, TripEnquiry Enquiry)>();
        var skipped = new List<AskSkippedDto>();
        var candidates = new List<(AskLegRequest Leg, TaxiRow Taxi)>();
        var created = new HashSet<Guid>();
        var live = new List<NotificationEvent>();

        // Distinct drivers of the request in leg order; a retry of the same clientRequestId never sends again.
        var requested = request.Legs.OrderBy(l => l.LegIndex).SelectMany(l => l.TaxiIds).Distinct().ToList();
        var clientRequestId = TripBulk.RequestId(request.ClientRequestId, http);
        var replayed = new HashSet<Guid>();
        if (clientRequestId is { } requestId)
        {
            foreach (var row in await unitOfWork.TripEnquiries.GetByClientRequestAsync(callerId, requestId, requested))
            {
                replayed.Add(row.ListingId);
                items.Add((TripEnquiryDtoBuilder.ReadContext(row.ContextJson).LegIndex ?? 1, row));
            }
        }
        var toProcess = requested.Where(id => !replayed.Contains(id)).ToList();
        // One query each for the taxis, the blocks and the traveller's own open rows (no per-driver lookups).
        var taxisById = await LoadTaxisAsync(db, toProcess);
        var blockedOwners = await TripBulk.BlockedOwnersAsync(db, callerId, taxisById.Values.Select(x => x.UserId).Distinct().ToList());
        var mineByTaxi = (await unitOfWork.TripEnquiries.GetOpenByTravellerListingsAsync(callerId, TripEnquiryKinds.Taxi, toProcess))
            .ToLookup(x => x.ListingId);
        var stamped = new HashSet<Guid>();
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

        // A driver is on a leg only when the ways network or his stored route serves its stretch: his fare comes from there.
        // Loaded once per leg before any lead is planned, so a driver who is not on the leg never replaces an older lead.
        var fareByLeg = await LoadLegFaresAsync(ways, journeys, journey, request.Legs, taxisById, replayed);

        foreach (var leg in request.Legs.OrderBy(l => l.LegIndex))
        {
            foreach (var taxiId in leg.TaxiIds.Where(id => !replayed.Contains(id)))
            {
                // One lead per leg and driver: the same driver on two legs gets a lead for each, with that leg's route.
                if (candidates.Any(c => c.Leg.LegIndex == leg.LegIndex && c.Taxi.Id == taxiId)
                    || items.Any(i => i.LegIndex == leg.LegIndex && i.Enquiry.ListingId == taxiId)) continue;
                taxisById.TryGetValue(taxiId, out var taxi);
                if (taxi == null || taxi.IsDeleted || taxi.Status != TaxiStatuses.Approved || !taxi.IsAvailable || taxi.UserId == callerId)
                {
                    skipped.Add(Skip(leg.LegIndex, taxiId, TripSkipReasons.NotAvailable));
                    continue;
                }
                if (blockedOwners.Contains(taxi.UserId))
                {
                    skipped.Add(Skip(leg.LegIndex, taxiId, TripSkipReasons.Blocked));
                    continue;
                }

                var mine = mineByTaxi[taxiId].ToList();
                if (segment != null)
                {
                    var pick = SegmentPick(segment, journey.Id, siblingIds, leg.LegIndex, taxiId, mine, openSegmentRows,
                        candidates.Select(c => (c.Leg.LegIndex, c.Taxi.Id)).ToList());
                    if (pick.Same != null) items.Add((leg.LegIndex, pick.Same));
                    else if (pick.Skip != null) skipped.Add(Skip(leg.LegIndex, taxiId, pick.Skip));
                    else if (!fareByLeg[leg.LegIndex].ContainsKey(taxiId)) skipped.Add(Skip(leg.LegIndex, taxiId, TripSkipReasons.NotAvailable));
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
                if (!fareByLeg[leg.LegIndex].ContainsKey(taxiId))
                {
                    skipped.Add(Skip(leg.LegIndex, taxiId, TripSkipReasons.NotAvailable));
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
        var leadPrices = LeadPrices(journey, candidates.Select(c => (c.Leg.LegIndex, c.Taxi.Id, c.Taxi.Seats)).ToList(), request.People, fareByLeg);

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
                    // The "withdrawn" push is an outbox row of this transaction (durable), one unique key per change.
                    TripBulk.StagePush(db, outbox,
                        TripEnquiryHandlers.EnquiryWithdrawn(old, new TripPushContext(old.OwnerUserId ?? Guid.Empty, travellerName, old.ListingName, null)),
                        old.Id, now, live, TripBulkRules.UpdateKey(old.Id, "wd", now));
                }
                await db.SaveChangesAsync();
            }

            foreach (var (leg, taxi) in candidates)
            {
                var saved = journey.Legs.FirstOrDefault(x => x.LegIndex == leg.LegIndex);
                var fromName = TaxiWaysLabels.Of(
                    string.IsNullOrWhiteSpace(leg.FromName) ? saved?.FromName : leg.FromName, null, leg.LegIndex == 1, false);
                var legTo = TaxiWaysLabels.Of(
                    string.IsNullOrWhiteSpace(leg.ToName) ? saved?.ToName : leg.ToName, null, false, true);
                var pickup = JourneyLeadText.PickupPlace(fromName, leg.LegIndex, leg.CarOf);
                var comesFrom = string.IsNullOrWhiteSpace(leg.ComesFrom) ? null : TripTextSafety.Clean(leg.ComesFrom);
                var price = leadPrices[(leg.LegIndex, taxi.Id)];
                if (segment != null)
                {
                    var (row, isNew) = await InsertSegmentRowAsync(
                        unitOfWork, db, segment, journey, leg, taxi, fromName, legTo, comesFrom, request.People,
                        travellerName.Length == 0 ? null : travellerName, expiresAt, isUrgent, now, price,
                        stamped.Add(taxi.Id) ? clientRequestId : null, request.StepKey);
                    if (row == null)
                    {
                        skipped.Add(Skip(leg.LegIndex, taxi.Id, TripSkipReasons.AlreadyOpen));
                        continue;
                    }
                    items.Add((leg.LegIndex, row));
                    if (isNew)
                    {
                        created.Add(row.Id);
                        TripBulk.StagePush(db, outbox, TripNotifications.TripLeadReceived(row, LeadPushContext(taxi, row, travellerName)), row.Id, now, live);
                    }
                    continue;
                }
                var context = new TripEnquiryContext
                {
                    City = toName,
                    Adults = request.People,
                    Kids = 0,
                    Rooms = null,
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
                    StepKey = request.StepKey,
                    ClientRequestId = stamped.Add(taxi.Id) ? clientRequestId : null,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                // No unique index covers a row without a trip (NULL is distinct), so parallel asks of the same car are serialised by an
                // advisory lock held to the end of the transaction, and the winner's row is read again before a second one is written.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({TripBulkRules.JourneyLeadLockKey(journey.Id, leg.LegIndex, taxi.Id)}, 0))");
                var racing = (await db.TripEnquiries.AsNoTracking()
                        .Where(x => x.TravellerUserId == callerId && x.ListingId == taxi.Id && x.Kind == TripEnquiryKinds.Taxi
                            && x.TripId == null && TripEnquiryStatuses.Open.Contains(x.Status)
                            && x.CheckIn < checkOut && x.CheckOut > date)
                        .ToListAsync())
                    .FirstOrDefault(x => IsLeadOf(x, journey.Id, leg.LegIndex));
                if (racing != null)
                {
                    items.Add((leg.LegIndex, racing));
                    continue;
                }
                db.TripEnquiries.Add(e);
                await db.SaveChangesAsync();

                await db.TaxiListings.Where(t => t.Id == taxi.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.TripLeadsReceived, x => x.TripLeadsReceived + 1));

                items.Add((leg.LegIndex, e));
                created.Add(e.Id);
                TripBulk.StagePush(db, outbox, TripNotifications.TripLeadReceived(e, LeadPushContext(taxi, e, travellerName)), e.Id, now, live);
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
            if (segment != null && items.Count > 0) await TripEnquiryHandlers.PromoteToPlannedAsync(db, segment.Trip.Id);
            if (segment != null && created.Count > 0)
                await unitOfWork.TripShortlist.MarkSentAsync(
                    segment.Trip.Id, request.StepKey, items.Where(i => created.Contains(i.Enquiry.Id)).Select(i => i.Enquiry.ListingId).ToList(), now);
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

        outbox.Wake();
        await notifier.PublishLiveAsync(live);
        if (segment != null)
            await TripBulk.CloseUnavailableAsync(
                db, segment.Trip.Id, request.StepKey, skipped.Select(x => (x.TaxiId, x.Reason)), now, loggers.CreateLogger("TripBulk"));

        var result = new AskDriversResultDto
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
            Results = TripBulk.BuildResults(
                requested, created, [], skipped.Select(x => (x.TaxiId, x.Reason)), items.Select(i => i.Enquiry), clientRequestId),
        };
        if (clientRequestId != null && created.Count == 0 && toProcess.Count == 0 && items.Count > 0)
        {
            http.Response.Headers[TripBulk.ReplayHeader] = "true";
            return OkResponse(result);
        }
        return CreatedResponse(result, $"/api/v1/taxis/journeys/{journey.Id}");
    }

    private static TripPushContext LeadPushContext(TaxiRow taxi, TripEnquiry row, string travellerName)
        => new(taxi.UserId, travellerName, row.ListingName, null);

    // Role = gettingThere | comingBack | segment; TravelDay = DayOf(start, Date), informational for the step roles (0 or N + 1 allowed).
    internal sealed record SegmentAsk(Trip Trip, TripPlan Plan, TripCityLeg Leg, int TravelDay, DateOnly Date, string Role = TripStepRoles.Segment);

    internal sealed record SegmentPickResult(TripEnquiry? Same, string? Skip, IReadOnlyList<Guid> Replaced);

    // Null segment = a journey no plan field references, or a step journey of a trip without dates: the trip-less path.
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

        var segment = link.Role == TripStepRoles.Segment;
        var leg = TripStepDates.LegFor(link.Role, plan, link.LegIndex);
        if (leg == null || (segment && link.TravelDay is not > 0)) return (null, BadRequestResponse("Unknown city"));
        if (trip.StartDate is not { } start)
        {
            if (!segment) return (null, null);
            return (null, ErrorResponse(422, "Add your trip dates first.", TripEnquiryErrorTypes.TripDatesRequired));
        }
        if (TripStepDates.Expected(link.Role, plan, link.LegIndex) is not { } date)
            return (null, ErrorResponse(422, "Add your trip dates first.", TripEnquiryErrorTypes.TripDatesRequired));
        if (journey.TravelDate != date)
            return (null, ErrorResponse(409, "Your trip date changed. Search again.", TripEnquiryErrorTypes.TravelDateChanged));
        if (trip.Status == TripStatuses.Completed) return (null, BadRequestResponse("This trip is completed"));
        return (new SegmentAsk(trip, plan, leg, TripExecutionRules.DayOf(start, date), date, link.Role), null);
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
        DateTime now, LeadPrice price, Guid? clientRequestId = null, string? stepKey = null)
    {
        var day = (short)segment.TravelDay;
        var context = TripSegmentContext.Build(
            segment.Trip, segment.Plan, segment.Leg, segment.TravelDay, segment.Date, journey.Id, leg.LegIndex, leg.CarOf,
            fromName, toName, comesFrom, people, price.Cars, travellerName, null, null, segment.Role);
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
            StepKey = stepKey,
            ClientRequestId = clientRequestId,
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

    // Server fares of the requested drivers per leg (JourneyLegFares); a driver without an entry is not on the leg. Only drivers that
    // can be asked at all are priced, the rest are skipped before.
    private static async Task<Dictionary<int, Dictionary<Guid, LegTaxiFare>>> LoadLegFaresAsync(
        ITaxiWaysSearch ways, ISavedJourneyRepository journeys, SavedJourney journey, IEnumerable<AskLegRequest> legs,
        IReadOnlyDictionary<Guid, TaxiRow> taxisById, IReadOnlySet<Guid> replayed)
    {
        var noTaxis = new Dictionary<Guid, JourneyTaxiInfo>();
        var result = new Dictionary<int, Dictionary<Guid, LegTaxiFare>>();
        foreach (var leg in legs)
        {
            var saved = journey.Legs.First(l => l.LegIndex == leg.LegIndex);
            var previousRoute = journey.Legs.FirstOrDefault(l => l.LegIndex == leg.LegIndex - 1)?.DriverRouteId;
            var ids = leg.TaxiIds.Where(id => !replayed.Contains(id) && taxisById.TryGetValue(id, out var t)
                && !t.IsDeleted && t.Status == TaxiStatuses.Approved && t.IsAvailable).Distinct().ToList();
            result[leg.LegIndex] = ids.Count == 0
                ? []
                : await JourneyLegFares.LoadAsync(ways, journeys, saved, ids, noTaxis, journey.Legs, previousRoute);
        }
        return result;
    }

    // Per (leg, taxi) of the new leads: cars from the saved leg's seat cover (JourneyLegPricing.CarsFor) and the server fare of
    // the stretch. The app's listedFare is never used.
    private static Dictionary<(int LegIndex, Guid TaxiId), LeadPrice> LeadPrices(
        SavedJourney journey, IReadOnlyList<(int LegIndex, Guid TaxiId, short? Seats)> asks, int people,
        IReadOnlyDictionary<int, Dictionary<Guid, LegTaxiFare>> fareByLeg)
    {
        var prices = new Dictionary<(int, Guid), LeadPrice>();
        foreach (var (legIndex, taxiId, seats) in asks)
        {
            var leg = journey.Legs.First(l => l.LegIndex == legIndex);
            var cars = JourneyLegPricing.CarsFor(taxiId, seats, people, leg.DriverTaxiId, leg.CoverTaxiIds);
            prices[(legIndex, taxiId)] = new LeadPrice(
                cars, fareByLeg[legIndex].TryGetValue(taxiId, out var f) ? JourneyLegPricing.LeadFare(f.PerCarFare, cars) : null);
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

    // One query for every requested driver.
    private static async Task<Dictionary<Guid, TaxiRow>> LoadTaxisAsync(ApplicationDbContext db, IReadOnlyCollection<Guid> taxiIds)
        => taxiIds.Count == 0
            ? []
            : (await db.TaxiListings.AsNoTracking()
                .Where(t => taxiIds.Contains(t.Id))
                .Select(t => new TaxiRow(
                    t.Id, t.UserId, t.User.Name, t.VehicleType != null ? t.VehicleType.Name : null, t.Status, t.IsAvailable, t.IsDeleted,
                    t.Latitude == null ? null : (double?)t.Latitude, t.Longitude == null ? null : (double?)t.Longitude, t.Seats))
                .ToListAsync()).ToDictionary(x => x.Id);

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
