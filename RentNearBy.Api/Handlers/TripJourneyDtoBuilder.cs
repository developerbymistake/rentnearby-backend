using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Handlers;

// The compact journey a trip shows for Getting there / Coming back. Phones only for drivers who are still public.
public static class TripJourneyDtoBuilder
{
    public static string? RoleOf(TripPlan plan, Guid journeyId)
    {
        if (plan.Steps?.GettingThere?.JourneyId == journeyId) return TripStepRoles.GettingThere;
        if (plan.Steps?.ComingBack?.JourneyId == journeyId) return TripStepRoles.ComingBack;
        return null;
    }

    public static SavedJourney? JourneyFor(TripPlan plan, string role, IEnumerable<SavedJourney> journeys)
        => TripStepRules.StepOf(plan.Steps, role)?.JourneyId is { } id ? journeys.FirstOrDefault(j => j.Id == id) : null;

    public static string? DisplayOf(TripPlan plan, string role, IEnumerable<SavedJourney> journeys)
        => plan.Steps == null ? null : TripStepRules.Display(TripStepRules.StepOf(plan.Steps, role), JourneyFor(plan, role, journeys));

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

    // Journeys referenced by a step first (Getting there, then Coming back), then any other linked one; at most two.
    public static List<TripJourneyDto> BuildMany(IReadOnlyList<SavedJourney> journeys, TripPlan plan, Dictionary<Guid, JourneyTaxiInfo> taxis)
        => journeys
            .Select(j => (Journey: j, Role: RoleOf(plan, j.Id)))
            .OrderBy(x => x.Role switch { TripStepRoles.GettingThere => 0, TripStepRoles.ComingBack => 1, _ => 2 })
            .Take(2)
            .Select(x => Build(x.Journey, x.Role, plan, taxis))
            .ToList();

    public static TripJourneyDto Build(SavedJourney j, string? role, TripPlan plan, Dictionary<Guid, JourneyTaxiInfo> taxis)
    {
        var expected = role == null ? null : TripStepRules.ExpectedDate(role, plan);
        return new TripJourneyDto
        {
            Id = j.Id,
            Role = role,
            FromName = j.FromName,
            FromLat = (double)j.FromLatitude,
            FromLng = (double)j.FromLongitude,
            ToName = j.ToName,
            ToLat = (double)j.ToLatitude,
            ToLng = (double)j.ToLongitude,
            Date = j.TravelDate,
            ExpectedDate = expected,
            DateChanged = role != null && TripStepRules.DateChanged(expected, j.TravelDate),
            StartChanged = role != null && TripStepRules.StartChanged(role, plan.Steps?.StartFrom, j),
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
