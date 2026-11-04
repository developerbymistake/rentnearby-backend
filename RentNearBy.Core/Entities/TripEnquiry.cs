using RentNearBy.Core.Models;

namespace RentNearBy.Core.Entities;

// One stay (or later taxi) enquiry of a trip. ListingId has no FK: listing rows are hard-deleted after the closure ran.
public class TripEnquiry
{
    public Guid Id { get; set; }
    public Guid? TripId { get; set; }
    public Guid? TravellerUserId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string Kind { get; set; } = TripEnquiryKinds.Stay;
    public Guid ListingId { get; set; }
    public string ListingName { get; set; } = string.Empty;
    public double? ListingLatitude { get; set; }
    public double? ListingLongitude { get; set; }
    public short? Day { get; set; }
    public string? DaysJson { get; set; }
    public DateOnly? CheckIn { get; set; }
    public DateOnly? CheckOut { get; set; }
    public Guid? ConversationId { get; set; }
    public string ContextJson { get; set; } = "{}";
    public string Status { get; set; } = TripEnquiryStatuses.Asked;
    public string? FailReason { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsUrgent { get; set; }
    public Guid? AcceptedOfferId { get; set; }
    public int? AgreedTotal { get; set; }
    public DateTime? ConfirmedByTravellerAt { get; set; }
    public DateTime? ConfirmedByOwnerAt { get; set; }
    public string? PhoneBookingJson { get; set; }

    public string? HostConfirmTokenHash { get; set; }
    public DateTime? HostConfirmTokenIssuedAt { get; set; }
    public DateTime? HostConfirmTokenExpiresAt { get; set; }
    public short HostConfirmLinkCount { get; set; }
    public DateTime? HostConfirmAnsweredAt { get; set; }
    public string? HostConfirmAnswer { get; set; }
    public string? HostConfirmAnsweredVia { get; set; }
    public Guid? HostConfirmAnsweredByUserId { get; set; }

    public short DriverFareEditCount { get; set; }
    public string AdvanceStatus { get; set; } = TripAdvanceStatuses.None;
    public int? AdvanceAmount { get; set; }
    public DateTime? HoldUntil { get; set; }
    public DateTime? AdvanceAskedAt { get; set; }
    public DateTime? ProofSentAt { get; set; }
    public DateTime? AdvanceReceivedAt { get; set; }
    public DateTime? AdvanceNotYetAt { get; set; }
    public DateTime? LastHostReminderAt { get; set; }
    public short HostReminderCount { get; set; }

    public int? ListedRateSnapshot { get; set; }
    public int? DriverFare { get; set; }
    public int? AgreedFare { get; set; }
    public string? DriverNote { get; set; }
    public DateTime? DriverRepliedAt { get; set; }
    public DateTime? BookedByTravellerAt { get; set; }
    public bool TollExtra { get; set; }
    public string? PickupTime { get; set; }
    public string? PickupPlace { get; set; }

    public DateTime? CancelledAt { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
