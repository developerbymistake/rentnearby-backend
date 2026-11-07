using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Handlers;

// The compact journey a trip shows for Getting there / Coming back / travel between cities. Phones only for drivers who are still public.
public static class TripJourneyDtoBuilder
{
    public const int MaxJourneys = 5;

    public static string? RoleOf(TripPlan plan, Guid journeyId) => TripJourneyLinks.Resolve(plan, journeyId)?.Role;

    public static SavedJourney? JourneyFor(TripPlan plan, string role, IEnumerable<SavedJourney> journeys)
        => TripStepRules.StepOf(plan.Steps, role)?.JourneyId is { } id ? journeys.FirstOrDefault(j => j.Id == id) : null;

    public static HashSet<Guid> TaxiIds(IEnumerable<SavedJourney> journeys)
        => journeys
            .SelectMany(j => j.Legs)
            .SelectMany(l => new[] { l.DriverTaxiId, l.Backup1TaxiId ?? Guid.Empty, l.Backup2TaxiId ?? Guid.Empty })
            .Where(g => g != Guid.Empty)
            .ToHashSet();

    public static async Task<Dictionary<Guid, JourneyTaxiInfo>> LoadTaxisAsync(ISavedJourneyRepository repo, IEnumerable<SavedJourney> journeys)
    {
        var ids = TaxiIds(journeys);
        return ids.Count == 0 ? new Dictionary<Guid, JourneyTaxiInfo>() : await repo.LoadTaxisAsync(ids);
    }

    // Getting there, Coming back, segments by city leg, then any other linked one; 2 steps + up to 3 segments.
    public static List<TripJourneyDto> BuildMany(
        IReadOnlyList<SavedJourney> journeys, TripPlan plan, Dictionary<Guid, JourneyTaxiInfo> taxis, IReadOnlyList<TripEnquiry> enquiries)
        => journeys
            .Select(j => (Journey: j, Link: TripJourneyLinks.Resolve(plan, j.Id)))
            .OrderBy(x => x.Link?.Role switch
            {
                TripStepRoles.GettingThere => 0,
                TripStepRoles.ComingBack => 1,
                TripStepRoles.Segment => 2,
                _ => 3,
            })
            .ThenBy(x => x.Link?.LegIndex ?? int.MaxValue)
            .Take(MaxJourneys)
            .Select(x => Build(x.Journey, x.Link, plan, taxis, enquiries))
            .ToList();

    public static TripJourneyDto Build(SavedJourney j, string? role, TripPlan plan, Dictionary<Guid, JourneyTaxiInfo> taxis)
        => Build(j, role == null ? null : new TripJourneyLink(role, null, null), plan, taxis, Array.Empty<TripEnquiry>());

    public static TripJourneyDto Build(
        SavedJourney j, TripJourneyLink? link, TripPlan plan, Dictionary<Guid, JourneyTaxiInfo> taxis, IReadOnlyList<TripEnquiry> enquiries)
    {
        var role = link?.Role;
        var segment = role == TripStepRoles.Segment;
        var expected = role == null ? null : TripStepDates.Expected(role, plan, link!.LegIndex);
        string? travelState = null;
        if (role != null)
        {
            var rows = TripSegmentRows.OfJourney(enquiries, j.Id);
            travelState = segment
                ? TripTravelState.Of(TripStepDates.LegFor(role, plan, link!.LegIndex)?.Arrival, j, rows, expected)
                : TripTravelState.OfStep(TripStepRules.StepOf(plan.Steps, role), j, rows, expected);
        }
        return new TripJourneyDto
        {
            Id = j.Id,
            Role = role,
            LegIndex = segment ? link!.LegIndex : null,
            TravelDay = segment ? link!.TravelDay : null,
            TravelState = travelState,
            FromName = j.FromName,
            FromLat = (double)j.FromLatitude,
            FromLng = (double)j.FromLongitude,
            ToName = j.ToName,
            ToLat = (double)j.ToLatitude,
            ToLng = (double)j.ToLongitude,
            Date = j.TravelDate,
            ExpectedDate = expected,
            DateChanged = role != null && TripStepRules.DateChanged(expected, j.TravelDate),
            StartChanged = role != null && !segment && TripStepRules.StartChanged(role, TripStepRules.EndOf(role, plan.Steps), j),
            People = j.People,
            Cars = j.Cars,
            Status = j.Status,
            ListedTotal = j.ListedTotal,
            AnyAsked = j.Legs.Any(l => l.LastEnquiryId != null),
            LastRefreshedAt = j.LastRefreshedAt,
            Legs = j.Legs.OrderBy(l => l.LegIndex).Select(l => Leg(l, j.People, taxis)).ToList(),
        };
    }

    private static TripJourneyLegDto Leg(SavedJourneyLeg l, int people, Dictionary<Guid, JourneyTaxiInfo> taxis)
    {
        taxis.TryGetValue(l.DriverTaxiId, out var driver);
        var backup = new[] { l.Backup1TaxiId, l.Backup2TaxiId }
            .Where(b => b is { } g && g != l.DriverTaxiId && taxis.TryGetValue(g, out var t) && t.IsPublic)
            .Select(b => taxis[b!.Value])
            .FirstOrDefault();
        var isPublic = driver?.IsPublic == true;
        return new TripJourneyLegDto
        {
            LegIndex = l.LegIndex,
            FromName = l.FromName,
            ToName = l.ToName,
            MeetingPlace = l.MeetingPlace,
            ListedFare = l.ListedFare,
            CarsNeeded = TaxiWaysRanking.CarsNeeded(people, driver?.Seats),
            Partial = l.Partial,
            DriverAvailable = l.DriverAvailable,
            LastKnownStatus = l.LastKnownStatus,
            LastEnquiryId = l.LastEnquiryId,
            LastQuotedFare = l.LastQuotedFare,
            TaxiId = l.DriverTaxiId,
            DriverName = driver?.DriverName ?? "Driver",
            VehicleTypeName = driver?.VehicleTypeName,
            Seats = driver?.Seats,
            PlateDisplay = driver?.PlateDisplay,
            Phone = isPublic ? driver!.Phone : null,
            WhatsApp = isPublic ? driver!.WhatsApp : null,
            BackupTaxiId = backup?.Id,
            BackupName = backup?.DriverName,
        };
    }
}
