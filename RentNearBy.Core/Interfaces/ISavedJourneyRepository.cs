using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public sealed record JourneyTaxiInfo(
    Guid Id, Guid UserId, string DriverName, string? VehicleTypeName, short? Seats, string? PlateDisplay, string? CityName,
    string TripReplyLabel, string? Phone, string? WhatsApp, bool IsPublic);

// Partial: the stretch is a part of the route, Fare its road-length share (TaxiFare.ForSpan).
public sealed record JourneyLegMatch(Guid TaxiId, Guid RouteId, int Fare, bool Partial = false);

public interface ISavedJourneyRepository
{
    Task AddAsync(SavedJourney journey);
    Task<int> CountLiveAsync(Guid userId);
    Task<IReadOnlyList<SavedJourney>> GetLiveByUserAsync(Guid userId);
    Task<SavedJourney?> GetOwnedAsync(Guid id, Guid userId, bool tracked);
    Task<SavedJourney?> FindByPlanAsync(Guid userId, string planKey, DateOnly date);

    // Public = not deleted, Approved, available. Hard-deleted ids are absent from the result.
    Task<Dictionary<Guid, JourneyTaxiInfo>> LoadTaxisAsync(IReadOnlyCollection<Guid> taxiIds);
    Task<Dictionary<Guid, TripEnquiry>> LoadEnquiriesAsync(IReadOnlyCollection<Guid> enquiryIds);

    // Public taxis with a route joining the two leg ends within 5 km (either direction), cheapest route per taxi.
    Task<IReadOnlyList<JourneyLegMatch>> MatchLegAsync(
        double fromLat, double fromLng, double toLat, double toLng, IReadOnlyCollection<Guid> excludeTaxiIds, int take);

    // Direct match first, else a route whose road line passes near both ends. Null = not on this leg or not public.
    Task<JourneyLegMatch?> FindOnLegAsync(Guid taxiId, double fromLat, double fromLng, double toLat, double toLng);

    // Journeys still to travel (TravelDate >= fromDate, not Completed, not deleted) that have work: a leg whose driver's
    // public state differs from DriverAvailable, or (eveDate set) TravelDate = eveDate with its dated eve key not logged.
    // Keyset page by Id after `after`, legs included, tracked. Next = null on the last page.
    Task<(List<SavedJourney> Items, Guid? Next)> GetWatchedPageAsync(DateOnly fromDate, DateOnly? eveDate, Guid? after, int take);

    // Trip steps: the caller's live journeys only.
    Task<List<SavedJourney>> GetOwnedByIdsAsync(IReadOnlyCollection<Guid> ids, Guid userId);
    Task<List<SavedJourney>> GetByTripAsync(Guid tripId, Guid userId);
    Task<Dictionary<Guid, List<SavedJourney>>> GetByTripsAsync(IReadOnlyCollection<Guid> tripIds, Guid userId);
    // Links keepIds to the trip and unlinks the caller's other journeys of that trip. Set-based, untouched rows stay as they are.
    Task RelinkTripAsync(Guid tripId, Guid userId, IReadOnlyCollection<Guid> keepIds);
    Task UnlinkTripAsync(Guid tripId);
    // Set-based; one journey's trip link (pre-link at save, unlink at delete).
    Task SetTripAsync(Guid journeyId, Guid? tripId);
    // Tracked segment rows (TripId set, Taxi, Day set) whose context carries this journey; any status.
    Task<List<TripEnquiry>> GetSegmentRowsAsync(Guid journeyId);
    Task<int> SaveChangesAsync();
}
