using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Buddy.Be2;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// GET /home/seeker-feed (contract 2.6 H2, AU-20). Budget 4, the same for 0, 1 and 3 trips: (1) TripSlots (count, limit, list);
// (2) the chosen trip by id; (3) its enquiries; (4) the trip's taxi request summary (only when a trip is chosen).
// Progress is TripEasyRows output (the same rows as the trip screen); the status line comes from the same enquiry rows.
public sealed class SeekerFeedReader(ApplicationDbContext db, TimeProvider clock)
{
    public async Task<SeekerFeedDto> ReadAsync(Guid userId, CancellationToken ct = default)
    {
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
        var slots = await TripSlots.ReadAsync(db, userId, ct);
        var dto = new SeekerFeedDto { Slots = new SeekerSlotsDto(slots.Count, slots.Limit, TripSlotRules.IsFull(slots.Count)) };

        var chosen = SeekerStatusRules.Choose(slots.Trips, t => t.StartDate, t => t.EndDate, t => t.Id, today);
        if (chosen is null) return dto;

        // Trip and enquiries are two statements: one joined statement would send the trip row (with PlanJson) once per enquiry.
        var trip = await db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == chosen.Id, ct);
        if (trip is null) return dto;
        var enquiries = await db.TripEnquiries.AsNoTracking().Where(e => e.TripId == trip.Id).ToListAsync(ct);

        var taxi = await TripTaxiRequestReads.ReadAsync(db, trip, ct);
        dto.TripCard = TripCard(trip, TripPlanReader.Read(trip), enquiries, taxi, today);
        dto.Status = Status(trip.Id, enquiries);
        return dto;
    }

    public static SeekerTripCardDto TripCard(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, TripTaxiRequestSummary? taxi, DateOnly today)
    {
        var progress = TripEasyRows.Compute(trip, plan, enquiries, null, null, taxi);
        var state = SeekerTripStates.Of(trip.StartDate, trip.EndDate, today);
        var card = new SeekerTripCardDto
        {
            State = state,
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
