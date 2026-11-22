using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Buddy.Be2;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// GET /home/seeker-feed (contract 2.6 H2, AU-20, D-23). Budget 4 for an owned trip, 2 for a joined trip or none:
// (1) TripSlots (count, limit); (2) the chosen trip, picked by its own one-row statement over every owned or joined trip
// (FB-58: never from the 3-row slots list); owner only: (3) its enquiries, projected; (4) the trip's taxi request summary.
// A joined viewer never gets the owner's enquiry data (FB-02): no status, no next step, no progress.
// Progress is TripEasyRows output (the same rows as the trip screen); the status line comes from the same enquiry rows.
public sealed class SeekerFeedReader(ApplicationDbContext db, TimeProvider clock)
{
    public async Task<SeekerFeedDto> ReadAsync(Guid userId, CancellationToken ct = default)
    {
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
        var slots = await TripSlots.ReadAsync(db, userId, ct);
        var dto = new SeekerFeedDto { Slots = new SeekerSlotsDto(slots.Count, slots.Limit, TripSlotRules.IsFull(slots.Count)) };
        if (slots.Count == 0) return dto;

        // Same order as SeekerStatusRules.Choose: not ended, start date (nulls last), end date, id.
        var mine = db.Trips.Where(t => t.OwnerUserId == userId).Select(t => t.Id)
            .Concat(db.Set<TripMember>().Where(m => m.UserId == userId && m.Status == TripMemberStatuses.Joined).Select(m => m.TripId));
        var trip = await db.Trips.AsNoTracking()
            .Where(t => mine.Contains(t.Id) && (t.EndDate == null || t.EndDate >= today))
            .OrderBy(t => t.StartDate == null ? 1 : 0).ThenBy(t => t.StartDate).ThenBy(t => t.EndDate).ThenBy(t => t.Id)
            .FirstOrDefaultAsync(ct);
        if (trip is null) return dto;

        if (trip.OwnerUserId != userId)
        {
            dto.TripCard = ViewerCard(trip, today);
            return dto;
        }

        // Trip and enquiries are two statements: one joined statement would send the trip row (with PlanJson) once per enquiry.
        var enquiries = await db.TripEnquiries.AsNoTracking().Where(e => e.TripId == trip.Id).Select(ForRows).ToListAsync(ct);
        var taxi = await TripTaxiRequestReads.ReadAsync(db, trip, ct);
        dto.TripCard = TripCard(trip, TripPlanReader.Read(trip), enquiries, taxi, today);
        dto.Status = Status(trip.Id, enquiries);
        return dto;
    }

    // The enquiry columns the trip rows and the status line read; the price breakdown json and free texts stay in the table.
    private static readonly System.Linq.Expressions.Expression<Func<TripEnquiry, TripEnquiry>> ForRows = e => new TripEnquiry
    {
        Id = e.Id, TripId = e.TripId, TravellerUserId = e.TravellerUserId, OwnerUserId = e.OwnerUserId, Kind = e.Kind,
        ListingId = e.ListingId, ListingName = e.ListingName, Day = e.Day, DaysJson = e.DaysJson, CityLeg = e.CityLeg,
        CheckIn = e.CheckIn, CheckOut = e.CheckOut, ContextJson = e.ContextJson, Status = e.Status, FailReason = e.FailReason,
        ExpiresAt = e.ExpiresAt, IsUrgent = e.IsUrgent, AcceptedOfferId = e.AcceptedOfferId, AgreedTotal = e.AgreedTotal,
        ConfirmedByTravellerAt = e.ConfirmedByTravellerAt, ConfirmedByOwnerAt = e.ConfirmedByOwnerAt,
        DriverFare = e.DriverFare, AgreedFare = e.AgreedFare, BookedByTravellerAt = e.BookedByTravellerAt,
        PickupTime = e.PickupTime, StepKey = e.StepKey, CancelledAt = e.CancelledAt, CreatedAt = e.CreatedAt, UpdatedAt = e.UpdatedAt,
    };

    // A joined viewer: the trip's own facts only (FB-02).
    public static SeekerTripCardDto ViewerCard(Trip trip, DateOnly today)
    {
        var state = SeekerTripStates.Of(trip.StartDate, trip.EndDate, today);
        var card = new SeekerTripCardDto
        {
            State = state,
            Role = TripSharingRoles.Viewer,
            TripId = trip.Id,
            Title = trip.Title,
            DestinationName = trip.CityName,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
        };
        if (state == SeekerTripStates.OnTrip && trip.StartDate is { } start) card.Today = Today(TripPlanReader.Read(trip), trip, start, today);
        return card;
    }

    public static SeekerTripCardDto TripCard(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, TripTaxiRequestSummary? taxi, DateOnly today)
    {
        var progress = TripEasyRows.Compute(trip, plan, enquiries, null, null, taxi);
        var state = SeekerTripStates.Of(trip.StartDate, trip.EndDate, today);
        var card = new SeekerTripCardDto
        {
            State = state,
            Role = TripSharingRoles.Owner,
            TripId = trip.Id,
            Title = trip.Title,
            DestinationName = trip.CityName,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            Done = progress.Done,
            Total = progress.Total,
            NextStep = progress.Next is { } next
                ? new SeekerNextStepDto(next.Key, SeekerNextStepKinds.Of(next.Kind), CityOf(plan, trip, next.LegIndex))
                : null,
        };
        if (state == SeekerTripStates.OnTrip && trip.StartDate is { } start) card.Today = Today(plan, trip, start, today);
        return card;
    }

    public static SeekerTodayDto? Today(TripPlan plan, Trip trip, DateOnly start, DateOnly today)
    {
        var days = plan.Days ?? [];
        var dayNumber = today.DayNumber - start.DayNumber + 1;
        var day = days.FirstOrDefault(d => d.Day == dayNumber);
        var places = (day?.Spots ?? []).Select(s => new SeekerPlaceDto(s.Id, s.Name)).ToList();
        return new SeekerTodayDto
        {
            DayNumber = dayNumber,
            TotalDays = Math.Max(days.Count, trip.EndDate is { } end ? end.DayNumber - start.DayNumber + 1 : days.Count),
            CityName = CityOf(plan, trip, day?.Leg),
            PlaceCount = places.Count,
            Places = places,
            PlacesSeen = null,
        };
    }

    public static SeekerStatusDto? Status(Guid tripId, IReadOnlyList<TripEnquiry> enquiries)
    {
        var facts = enquiries.Where(e => e.Kind == TripEnquiryKinds.Stay)
            .Select(e => new SeekerEnquiryFact(e.Id, e.Status, e.ListingName, e.UpdatedAt)).ToList();
        var s = SeekerStatusRules.Of(facts);
        return s is null ? null : new SeekerStatusDto(s.Kind, s.Count, s.ListingName, s.EnquiryId, tripId, s.Key);
    }

    private static string? CityOf(TripPlan plan, Trip trip, int? legIndex)
    {
        if (plan.Legs is not { Count: > 0 } legs) return trip.CityName;
        var leg = legIndex is { } k ? legs.FirstOrDefault(l => l.Index == k) : null;
        return (leg ?? legs[0]).CityName;
    }
}
