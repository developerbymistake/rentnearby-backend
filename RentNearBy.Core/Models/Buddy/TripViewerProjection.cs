using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Core.Models.Buddy;

public static class TripPartWords
{
    public const string NotAdded = "NotAdded";
    public const string Arranging = "Arranging";
    public const string Booked = "Booked";
    public const string Completed = "Completed";

    // The words of the owner's card (S15): notAdded | arranging | booked.
    public static string Card(string state) => state switch
    {
        Booked or Completed => "booked",
        Arranging => "arranging",
        _ => "notAdded",
    };
}

// What the viewer projection is built from; every field is copied field by field onto the allow list (contract 2.2 S14).
public sealed record TripViewerSource(
    Trip Trip, TripPlan Plan, string Role, string OwnerName, string? OwnerUsername,
    int TravellerCount, IReadOnlyList<TravellerChipDto> FirstTravellers,
    IReadOnlyList<TripEnquiry> Enquiries, TripTaxiRequestSummary? TaxiRequest, TripMoneyCard? Money, DateOnly TodayIst,
    string? OwnerPhotoUrl = null);

// Pure: builds TripViewerDto from an ALLOW LIST. Never copies raw PlanJson, guide, marks, steps, enquiry or offer rows,
// listing ids, conversation ids, phones, plates, prices, plan lock, taxi request ids, invite code, mute, adults or kids.
public static class TripViewerProjection
{
    private static readonly string[] OpenStay =
    [
        TripEnquiryStatuses.Asked, TripEnquiryStatuses.Offered, TripEnquiryStatuses.Countered,
        TripEnquiryStatuses.FareSent, TripEnquiryStatuses.BookedByTraveller,
    ];

    public static TripViewerDto Build(TripViewerSource s)
    {
        var trip = s.Trip;
        var plan = s.Plan;
        return new TripViewerDto
        {
            TripId = trip.Id,
            Title = trip.Title,
            Status = trip.Status,
            Role = s.Role,
            Owner = new TripOwnerRefDto { Name = s.OwnerName, Username = s.OwnerUsername, PhotoUrl = s.OwnerPhotoUrl },
            LegCities = plan.Legs is { Count: > 0 } ? plan.LegCities : [trip.CityName],
            DayCount = trip.DayCount,
            SpotCount = trip.SpotCount,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            TravellerCount = s.TravellerCount,
            FirstTravellers = s.FirstTravellers.Take(3).Select(c => new TravellerChipDto { UserId = c.UserId, Initial = c.Initial, Name = c.Name, PhotoUrl = c.PhotoUrl }).ToList(),
            Legs = Legs(trip, plan),
            Days = Days(trip, plan),
            Stay = Stay(plan, trip, s.Enquiries),
            Taxi = Taxi(s.Enquiries, s.TaxiRequest),
            ProgressPercent = Progress(trip, plan, s.Enquiries, s.TaxiRequest),
            Expenses = Card(s.Money, trip.StartDate, trip.DayCount, s.TodayIst),
            UpdatedAt = trip.UpdatedAt,
        };
    }

    public static TripExpenseCardDto Card(TripMoneyCard? money, DateOnly? startDate, int dayCount, DateOnly todayIst) => new()
    {
        HasExpenses = money?.HasExpenses ?? false,
        Spent = money?.Spent ?? 0,
        MyShare = money?.MyShare ?? 0,
        DayToday = TripSharingRules.DayToday(startDate, dayCount, todayIst),
        OpenPayments = money?.OpenPayments ?? 0,
        Settled = money?.Settled ?? true,
    };

    public static TripSharedMoneyDto SharedCard(TripMoneyCard? money) => new()
    {
        HasExpenses = money?.HasExpenses ?? false,
        Spent = money?.Spent ?? 0,
        MyShare = money?.MyShare ?? 0,
        OpenPayments = money?.OpenPayments ?? 0,
        Settled = money?.Settled ?? true,
    };

    public static int Progress(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, TripTaxiRequestSummary? taxi)
        => TripEasyRows.Compute(trip, plan, enquiries, [], null, taxi).Percent;

    public static List<TripViewerLegDto> Legs(Trip trip, TripPlan plan)
    {
        if (plan.Legs is not { Count: > 0 })
        {
            var days = plan.Days ?? [];
            return
            [
                new TripViewerLegDto
                {
                    Index = 1, CityName = trip.CityName, CityKey = trip.CityKey, Latitude = trip.CityLatitude, Longitude = trip.CityLongitude,
                    FirstDay = days.Count > 0 ? days.Min(d => d.Day) : 0, LastDay = days.Count > 0 ? days.Max(d => d.Day) : 0,
                    Nights = days.Count, SpotCount = days.Sum(d => d.Spots?.Count ?? 0),
                },
            ];
        }
        return plan.Legs.Select(l =>
        {
            var first = plan.LegStart(l.Index);
            var last = plan.LegEnd(l.Index);
            return new TripViewerLegDto
            {
                Index = l.Index,
                CityName = l.CityName,
                CityKey = l.CityKey,
                Latitude = l.Latitude,
                Longitude = l.Longitude,
                FirstDay = first,
                LastDay = last,
                Nights = first == 0 ? 0 : last - first + 1,
                SpotCount = plan.DaysOf(l.Index).Sum(d => d.Spots?.Count ?? 0),
            };
        }).ToList();
    }

    // Times and road minutes are not stored with the plan; the app lays the day out as it does for the owner.
    // RouteGeoJson stays null here; TripViewerRoutes fills it from the road cache.
    public static List<TripViewerDayDto> Days(Trip trip, TripPlan plan)
        => (plan.Days ?? []).Where(d => d != null).OrderBy(d => d.Day).Select(d => new TripViewerDayDto
        {
            Day = d.Day,
            Leg = plan.LegIndexOf(d.Day),
            RestDay = d.RestDay == true,
            Date = trip.StartDate?.AddDays(d.Day - 1),
            TotalMinutes = (d.Spots ?? []).Sum(p => p.VisitMinutes),
            PlaceCount = d.Spots?.Count ?? 0,
            Spots = (d.Spots ?? []).Select(p => new TripViewerSpotDto
            {
                Id = p.Id,
                Name = p.Name,
                Category = p.Category,
                Latitude = p.Latitude,
                Longitude = p.Longitude,
                VisitMinutes = p.VisitMinutes,
            }).ToList(),
        }).ToList();

    public static TripViewerStayDto Stay(TripPlan plan, Trip trip, IReadOnlyList<TripEnquiry> enquiries)
    {
        var stays = enquiries.Where(e => e.Kind == TripEnquiryKinds.Stay).ToList();
        var booked = stays.Where(e => TripEnquiryStatuses.IsBooked(e.Status)).OrderBy(e => e.CheckIn).ThenBy(e => e.Id).ToList();
        if (booked.Count > 0)
            return new TripViewerStayDto
            {
                State = TripPartWords.Booked,
                Items = booked.Select(e =>
                {
                    var leg = e.CityLeg is { } cl && cl > 0 ? cl : e.Day is { } day ? plan.LegIndexOf(day) : 1;
                    var city = plan.Legs is { Count: > 0 } legs && leg <= legs.Count ? legs[leg - 1].CityName : trip.CityName;
                    return new TripViewerStayItemDto
                    {
                        Leg = leg,
                        CityName = city,
                        StayName = e.ListingName,
                        Nights = e.CheckIn is { } a && e.CheckOut is { } b ? Math.Max(0, b.DayNumber - a.DayNumber) : 0,
                        CheckIn = e.CheckIn,
                        CheckOut = e.CheckOut,
                    };
                }).ToList(),
            };
        return new TripViewerStayDto
        {
            State = stays.Any(e => OpenStay.Contains(e.Status)) ? TripPartWords.Arranging : TripPartWords.NotAdded,
        };
    }

    public static TripViewerTaxiDto Taxi(IReadOnlyList<TripEnquiry> enquiries, TripTaxiRequestSummary? request)
    {
        if (request?.Booking is { } b && b.Status != BookingStates.Cancelled)
            return new TripViewerTaxiDto
            {
                State = b.Status == BookingStates.Completed ? TripPartWords.Completed : TripPartWords.Booked,
                VehicleType = string.IsNullOrWhiteSpace(b.VehicleName) ? null : b.VehicleName,
                DriverFirstName = TripSharingRules.FirstWord(b.DriverName),
                PickupDate = b.DateFrom,
            };
        if (request is { IsActive: true }) return new TripViewerTaxiDto { State = TripPartWords.Arranging };
        var taxis = enquiries.Where(e => e.Kind == TripEnquiryKinds.Taxi).ToList();
        if (taxis.Any(e => e.Status is TripEnquiryStatuses.ConfirmedByBoth or TripEnquiryStatuses.BookedByTraveller))
            return new TripViewerTaxiDto { State = TripPartWords.Booked };
        if (taxis.Any(e => OpenStay.Contains(e.Status))) return new TripViewerTaxiDto { State = TripPartWords.Arranging };
        return new TripViewerTaxiDto();
    }
}

// Bug 15: the viewer gets the same road line the owner sees, over the day's spots only (no stay anchor, which is not
// shared), through the planner's road cache (trip:route:{sha256}); OSRM down or no route leaves null.
public static class TripViewerRoutes
{
    // Days with two or more spots, in day order, the first maxDays of them (the planner's RouteGeometryMaxDays cap).
    public static List<(TripViewerDayDto Day, IReadOnlyList<(double Lat, double Lon)> Points)> RouteDays(IReadOnlyList<TripViewerDayDto> days, int maxDays)
        => days.Where(d => d.Spots.Count >= 2)
            .Take(Math.Max(0, maxDays))
            .Select(d => (d, (IReadOnlyList<(double Lat, double Lon)>)d.Spots.Select(s => (s.Latitude, s.Longitude)).ToList()))
            .ToList();

    public static async Task FillAsync(IReadOnlyList<TripViewerDayDto> days, ITripRoadCache roads, int maxDays, CancellationToken ct = default)
    {
        var routes = RouteDays(days, maxDays);
        if (routes.Count == 0) return;
        var geometries = await roads.GetRouteGeometriesAsync(routes.Select(r => r.Points).ToList(), ct);
        for (var k = 0; k < routes.Count; k++)
            routes[k].Day.RouteGeoJson = geometries[k];
    }
}
