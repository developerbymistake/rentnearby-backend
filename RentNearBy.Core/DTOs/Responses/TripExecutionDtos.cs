using RentNearBy.Core.Models;

namespace RentNearBy.Core.DTOs.Responses;

public class TripEnquiryDto
{
    public Guid Id { get; set; }
    public Guid? TripId { get; set; }
    public string Kind { get; set; } = TripEnquiryKinds.Stay;
    public Guid ListingId { get; set; }
    public string ListingName { get; set; } = string.Empty;
    public double? ListingLatitude { get; set; }
    public double? ListingLongitude { get; set; }
    public int? Day { get; set; }
    public int[]? Days { get; set; }
    public int? CityLeg { get; set; }
    public DateOnly? CheckIn { get; set; }
    public DateOnly? CheckOut { get; set; }
    public int Nights { get; set; }
    // yyyy-MM-dd of the trip (null without a trip): first night and the latest allowed check-out (last day + 1).
    public string? TripStartDate { get; set; }
    public string? TripCheckOutLimit { get; set; }
    public Guid? ConversationId { get; set; }
    public string Status { get; set; } = TripEnquiryStatuses.Asked;
    public string? FailReason { get; set; }
    public string DisplayStatus { get; set; } = TripDisplayStatuses.Asked;
    public DateTime ExpiresAt { get; set; }
    public bool IsUrgent { get; set; }
    public int? AgreedTotal { get; set; }
    public TripOfferDto? AcceptedOffer { get; set; }
    // Newest Pending row of kind Offer only; counters, revised offers and change requests have their own fields.
    public TripOfferDto? OpenOffer { get; set; }
    public int OfferCount { get; set; }
    public TripOfferDto? PendingCounter { get; set; }
    public TripOfferDto? PendingRevised { get; set; }
    public TripOfferDto? PendingChange { get; set; }
    public int CounterCount { get; set; }
    public int RevisedCount { get; set; }
    public TripEnquiryContextDto Context { get; set; } = new();
    // A journey car between two cities of the trip (Day = travel day).
    public bool IsSegment { get; set; }
    // Derived on every read from the row's dates and the current plan, traveller only; never stored.
    public bool WrongCity { get; set; }
    public int WrongNights { get; set; }
    public int WrongTotal { get; set; }
    public int? WrongFirstDay { get; set; }
    public string? WrongDayCity { get; set; }
    public bool OutsideTrip { get; set; }
    public TripAdvanceDto Advance { get; set; } = new();
    public TripTaxiDto? Taxi { get; set; }
    public TripPartyDto Traveller { get; set; } = new();
    public TripOwnerPartyDto Owner { get; set; } = new();
    public string? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TripEnquiryContextDto
{
    public string City { get; set; } = string.Empty;
    public int? Adults { get; set; }
    public int? Kids { get; set; }
    public int? Rooms { get; set; }
    public List<TripEnquiryContextDay> Days { get; set; } = new();
    public string? PickupTime { get; set; }
    public string? PickupPlace { get; set; }
    public string Text { get; set; } = string.Empty;
    public int? CityIndex { get; set; }
    public int? CityCount { get; set; }
    public Guid? JourneyId { get; set; }
    // The car (journey leg) 1..3, never the city leg.
    public int? JourneyLegIndex { get; set; }
}

public class TripAdvanceDto
{
    public string Status { get; set; } = TripAdvanceStatuses.None;
    public int? Amount { get; set; }
    public DateTime? HoldUntil { get; set; }
    public DateTime? AskedAt { get; set; }
    public DateTime? ProofSentAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public DateTime? NotYetAt { get; set; }
    public DateTime? LastHostReminderAt { get; set; }
    public DateTime? NextHostReminderAt { get; set; }
    public int ReminderCount { get; set; }
    // Paid advance the host owes back (paid beyond the agreed total, or a night the host failed); null when nothing.
    public int? RefundDue { get; set; }
}

public class TripTaxiDto
{
    public int? ListedRate { get; set; }
    public int? ListedTotal { get; set; }
    public int? DriverFare { get; set; }
    public string? DriverNote { get; set; }
    public DateTime? DriverRepliedAt { get; set; }
    public int FareEditCount { get; set; }
    public int FareEditsLeft { get; set; }
    public int? AgreedFare { get; set; }
    public bool TollExtra { get; set; }
    public string? PickupTime { get; set; }
    public string? PickupPlace { get; set; }
    public DateTime? BookedAt { get; set; }
}

public class TripDriverCountsDto
{
    public int New { get; set; }
    public int Replied { get; set; }
    public int Booked { get; set; }
    public int Closed { get; set; }
}

public class TripDriverLeadPageDto
{
    public List<TripEnquiryDto> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public TripDriverCountsDto Counts { get; set; } = new();
}

public class TripPartyDto
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
}

public class TripOwnerPartyDto
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? WhatsApp { get; set; }
}

public class TripOfferDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = TripOfferKinds.Offer;
    public DateOnly? CheckIn { get; set; }
    public DateOnly? CheckOut { get; set; }
    public int Nights { get; set; }
    public List<TripOfferLine> Lines { get; set; } = new();
    public int Total { get; set; }
    public string? Inclusions { get; set; }
    public int? AdvanceAmount { get; set; }
    public string? BalanceNote { get; set; }
    public string? CancellationText { get; set; }
    public bool? GstInvoice { get; set; }
    public string? Note { get; set; }
    // Revised only: same value as Note.
    public string? Reason { get; set; }
    public int? Adults { get; set; }
    public int? Kids { get; set; }
    // The locked AgreedTotal while a Revised / ChangeRequest row is Pending, else null.
    public int? OldTotal { get; set; }
    // Counter only: the guest's per-room-per-night price (lines are rescaled to the host's room mix).
    public int? ProposedPerNight { get; set; }
    public DateTime? ValidUntil { get; set; }
    public string Status { get; set; } = TripOfferStatuses.Pending;
    public DateTime? RespondedAt { get; set; }
    public Guid? MessageId { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool DatesDiffer { get; set; }
}

public class TripSkippedDto
{
    public Guid ListingId { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class TripEnquiryBatchDto
{
    public List<TripEnquiryDto> Items { get; set; } = new();
    public List<TripSkippedDto> Skipped { get; set; } = new();
    // Open leads whose days were merged with this request; they are in Items too.
    public List<Guid> Updated { get; set; } = new();
    public bool VeryShortNotice { get; set; }
}

public class TripOwnerCountsDto
{
    public int New { get; set; }
    public int Offered { get; set; }
    public int Confirmed { get; set; }
    public int Closed { get; set; }
}

public class TripOwnerEnquiryPageDto
{
    public List<TripEnquiryDto> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public TripOwnerCountsDto Counts { get; set; } = new();
}

public class TripOfferResultDto
{
    public TripEnquiryDto Enquiry { get; set; } = new();
    public TripOfferDto Offer { get; set; } = new();
}

public class TripNightConflictDto
{
    public Guid EnquiryId { get; set; }
    public string ListingName { get; set; } = string.Empty;
}
