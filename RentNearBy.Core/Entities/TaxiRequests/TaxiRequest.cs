using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Core.Entities.TaxiRequests;

// One traveller request for a local taxi on a trip. Transitions are conditional updates, so no xmin.
public class TaxiRequest
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public Guid TravellerUserId { get; set; }
    public string Status { get; set; } = RequestStates.Open;
    public string? EndReason { get; set; }
    public string? CancelChip { get; set; }
    public string? AdminNote { get; set; }

    public bool WantPickup { get; set; }
    public bool WantSightseeing { get; set; }
    public bool WantDrop { get; set; }
    public long? PickupCityKey { get; set; }
    public string? PickupCityName { get; set; }
    public long? DropCityKey { get; set; }
    public string? DropCityName { get; set; }
    public bool DropSameAsPickup { get; set; }

    // Pool and lock key: pickup city, else drop city, else first city.
    public long DispatchCityKey { get; set; }
    public long MatchToCityKey { get; set; }
    public long? MatchFromCityKey { get; set; }

    public short Seats { get; set; }
    public short Travellers { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string SightseeingDaysJson { get; set; } = "[]";
    public string TripSnapshotJson { get; set; } = "{}";

    public short AcceptTarget { get; set; } = TaxiDispatchRules.DefaultAcceptTarget;
    public short AcceptedCount { get; set; }
    public Guid? SelectedOfferId { get; set; }
    public long Version { get; set; } = 1;
    public DateTime? QueueWaitStartedAt { get; set; }
    public int WaitRound { get; set; }
    public DateTime? QueueExhaustedAt { get; set; }

    public Guid? ClientRequestId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}
