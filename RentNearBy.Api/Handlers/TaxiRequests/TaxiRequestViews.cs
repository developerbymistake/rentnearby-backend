using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Api.Handlers.TaxiRequests;

// One offer with the driver facts the card needs (read live from the listing).
public sealed record TaxiViewOffer(
    Guid Id, string Status, int SeqNo, DateTime? ConfirmBy, string DriverName, string? ContactPhone, string Vehicle,
    int Seats, int RatingSum, int RatingCount, int RidesCompleted, bool IsFeatured = false, string? DisplayPhotoUrl = null);

public sealed record TaxiViewBooking(Guid Id, string Status, DateOnly DateFrom, DateOnly DateTo, DateTime? StartedAt = null);

public sealed record TaxiViewRating(int Stars, DateTime RatedAt);

// Everything the traveller object needs, already read. The builder below is pure, so a test feeds it in-memory rows.
public sealed record TaxiViewInput(
    TaxiRequest Request,
    string TripName,
    int DayCount,
    int AskedCount,
    IReadOnlyList<TaxiViewOffer> Offers,
    bool AnyOfferMissedOrDeclined,
    TaxiViewBooking? Booking,
    TaxiViewRating? Rating,
    DateOnly TodayIst);

// Contract section 5 `TaxiRequest` object. The view comes from TaxiRequestViewRules, the driver phone only while the booking is live.
public sealed class TaxiRequestViews(ApplicationDbContext db)
{
    public static TaxiRequestDto Build(TaxiViewInput input)
    {
        var r = input.Request;
        var ringing = input.Offers.Count(o => o.Status == OfferStates.Ringing);
        var bookingLive = input.Booking is { Status: BookingStates.Booked or BookingStates.InProgress };
        DateTime? waitUntil = RequestStates.Dispatching.Contains(r.Status) && r.QueueWaitStartedAt is { } started
            ? started.AddSeconds(TaxiDispatchRules.QueueWaitSeconds)
            : null;
        var view = TaxiRequestViewRules.Derive(r.Status, r.EndReason, input.AnyOfferMissedOrDeclined, ringing > 0, waitUntil, input.Rating is not null);

        return new TaxiRequestDto
        {
            Id = r.Id,
            TripId = r.TripId,
            TripName = input.TripName,
            State = r.Status,
            View = view,
            Version = r.Version,
            EndReason = r.EndReason,
            Sections = new TaxiSectionsDto { Pickup = r.WantPickup, Sightseeing = r.WantSightseeing, Drop = r.WantDrop },
            Pickup = r.WantPickup && r.PickupCityKey is { } pk ? new TaxiCityRefDto { CityKey = pk, Name = r.PickupCityName ?? string.Empty } : null,
            Drop = r.WantDrop && r.DropCityKey is { } dk
                ? new TaxiRequestDropDto { CityKey = dk, Name = r.DropCityName ?? string.Empty, SameAsPickup = r.DropSameAsPickup }
                : null,
            Seats = r.Seats,
            Travellers = r.Travellers,
            DateFrom = r.DateFrom,
            DateTo = r.DateTo,
            DayCount = input.DayCount,
            SightseeingDays = ReadDays(r.SightseeingDaysJson),
            AskedCount = input.AskedCount,
            RingingCount = ringing,
            AcceptedCount = r.AcceptedCount,
            StillAsking = TaxiDispatchRules.StillAsking(r.Status, r.QueueExhaustedAt is not null, ringing > 0, r.AcceptedCount, r.AcceptTarget),
            CanAskMore = TaxiDispatchRules.CanAskMore(r.Status, r.AcceptedCount, r.AcceptTarget, r.QueueExhaustedAt is not null),
            CanCancel = r.Status is RequestStates.Open or RequestStates.Collecting or RequestStates.Selected or RequestStates.Booked,
            WaitUntil = waitUntil,
            Drivers = input.Offers
                .Where(o => OfferStates.TravellerVisible.Contains(o.Status))
                .OrderBy(o => o.SeqNo)
                .Select(o => Driver(o, r.Status, bookingLive))
                .ToList(),
            Booking = input.Booking is { } b ? Booking(b, r, input.Rating is not null, input.TodayIst) : null,
            Rating = input.Rating is { } rt ? new TaxiRequestRatingDto { Stars = rt.Stars, RatedAt = rt.RatedAt } : null,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
        };
    }

    // Contact goes to the traveller for an Accepted or Selected driver, and for the Confirmed one only while the booking is live.
    private static TaxiRequestDriverDto Driver(TaxiViewOffer o, string requestState, bool bookingLive)
    {
        var phoneVisible = o.Status is OfferStates.Accepted or OfferStates.Selected || (o.Status == OfferStates.Confirmed && bookingLive);
        return new TaxiRequestDriverDto
        {
            OfferId = o.Id,
            State = o.Status,
            Name = o.DriverName,
            Phone = phoneVisible && !string.IsNullOrWhiteSpace(o.ContactPhone) ? o.ContactPhone : null,
            Vehicle = o.Vehicle,
            Seats = o.Seats,
            RatingAverage = TaxiBookingService.RatingAverage(o.RatingSum, o.RatingCount),
            RatingCount = o.RatingCount,
            RidesCompleted = o.RidesCompleted,
            CanSelect = requestState == RequestStates.Collecting && o.Status == OfferStates.Accepted,
            ConfirmBy = o.Status == OfferStates.Selected ? o.ConfirmBy : null,
            IsFeatured = o.IsFeatured,
            DisplayPhotoUrl = CloudinaryThumb.Apply(o.DisplayPhotoUrl, CloudinaryThumb.TaxiFace),
        };
    }

    private static TaxiRequestBookingDto Booking(TaxiViewBooking b, TaxiRequest r, bool rated, DateOnly todayIst) => new()
    {
        Id = b.Id,
        State = b.Status,
        DateFrom = b.DateFrom,
        DateTo = b.DateTo,
        DayNumber = b.Status == BookingStates.InProgress
            ? Math.Clamp(todayIst.DayNumber - b.DateFrom.DayNumber + 1, 1, b.DateTo.DayNumber - b.DateFrom.DayNumber + 1)
            : null,
        CanCancel = b.Status == BookingStates.Booked,
        CanEnd = b.Status == BookingStates.InProgress,
        CanRate = !rated && TaxiRatingRules.CanRate(r.Status, r.EndReason, b.Status, b.StartedAt),
        LeftDuringTrip = TaxiRatingRules.LeftDuringTrip(r.Status, r.EndReason, b.Status, b.StartedAt),
    };

    private static List<int> ReadDays(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<int>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // Reads the rows around one request (trip title, offers with driver facts, latest booking, its rating) and builds the object.
    public async Task<TaxiRequestDto> BuildAsync(TaxiRequest request, CancellationToken ct = default)
    {
        var snapshot = TaxiTripSnapshot.FromJson(request.TripSnapshotJson);
        var title = await db.Trips.AsNoTracking().Where(t => t.Id == request.TripId).Select(t => t.Title).FirstOrDefaultAsync(ct);

        var offerRows = await (
            from o in db.Set<TaxiRequestOffer>().AsNoTracking()
            where o.RequestId == request.Id
            join l in db.TaxiListings.AsNoTracking() on o.DriverListingId equals l.Id into ls
            from l in ls.DefaultIfEmpty()
            join u in db.Users.AsNoTracking() on o.DriverUserId equals u.Id into us
            from u in us.DefaultIfEmpty()
            select new
            {
                o.Id,
                o.Status,
                o.SeqNo,
                o.ConfirmBy,
                Name = u != null ? u.Name : null,
                Phone = l != null ? l.ContactPhone : null,
                Vehicle = l != null && l.VehicleType != null ? l.VehicleType.Name : null,
                Seats = l != null ? l.Seats : null,
                Sum = l != null ? l.RatingSum : 0,
                Count = l != null ? l.RatingCount : 0,
                Rides = l != null ? l.RidesCompleted : 0,
                Featured = l != null && l.IsFeatured,
                DisplayPhotoUrl = l != null ? l.DisplayPhotoUrl : null,
            }).ToListAsync(ct);

        var offers = offerRows.Select(o => new TaxiViewOffer(o.Id, o.Status, o.SeqNo, o.ConfirmBy, o.Name ?? string.Empty, o.Phone,
            o.Vehicle ?? string.Empty, o.Seats ?? 0, o.Sum, o.Count, o.Rides, o.Featured, o.DisplayPhotoUrl)).ToList();

        var booking = await db.Set<TaxiBooking>().AsNoTracking()
            .Where(b => b.RequestId == request.Id)
            .OrderByDescending(b => b.BookedAt)
            .Select(b => new TaxiViewBooking(b.Id, b.Status, b.DateFrom, b.DateTo, b.StartedAt))
            .FirstOrDefaultAsync(ct);
        TaxiViewRating? rating = null;
        if (booking is not null)
            rating = await db.Set<TaxiRating>().AsNoTracking().Where(x => x.BookingId == booking.Id)
                .Select(x => new TaxiViewRating(x.Stars, x.CreatedAt)).FirstOrDefaultAsync(ct);

        var dayCount = snapshot?.Days.Count is > 0 ? snapshot.Days.Count : request.DateTo.DayNumber - request.DateFrom.DayNumber + 1;
        var input = new TaxiViewInput(request, string.IsNullOrEmpty(title) ? snapshot?.Name ?? string.Empty : title, dayCount, offers.Count,
            offers, offers.Any(o => o.Status is OfferStates.Missed or OfferStates.Declined), booking, rating, IstTime.TodayIst(DateTime.UtcNow));
        return Build(input);
    }

    // The active request of the trip, else the latest one; null when the trip never had one.
    public async Task<TaxiRequest?> CurrentForTripAsync(Guid tripId, CancellationToken ct = default)
    {
        var rows = db.Set<TaxiRequest>().AsNoTracking().Where(r => r.TripId == tripId);
        var active = await rows.Where(r => RequestStates.Active.Contains(r.Status)).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        return active ?? await rows.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
    }
}
