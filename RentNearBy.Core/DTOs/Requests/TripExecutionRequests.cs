using RentNearBy.Core.Models;

namespace RentNearBy.Core.DTOs.Requests;

public class CreateTripEnquiryRequest
{
    public string Kind { get; set; } = TripEnquiryKinds.Stay;
    // Stay only.
    public int Day { get; set; }
    // Taxi only: chosen trip days.
    public List<int>? Days { get; set; }
    public List<Guid> ListingIds { get; set; } = new();
    public bool AllNights { get; set; }
    // Stay only: explicit nights from Day (capped at the trip's last night); when set, AllNights is ignored.
    public int? Nights { get; set; }
    // Taxi only, "HH:mm"; null = from the trip settings.
    public string? PickupTime { get; set; }
    public string? PickupPlace { get; set; }
}

// POST /trips/{tripId}/taxi-bookings ("I booked"). The traveller's note stays in the plan, never sent.
public class CreateTaxiBookingRequest
{
    public Guid TaxiId { get; set; }
    public List<int> Days { get; set; } = new();
    public int? AgreedFare { get; set; }
    public bool TollExtra { get; set; }
    public string PickupTime { get; set; } = string.Empty;
    public string? PickupPlace { get; set; }
    public string? Note { get; set; }
    public bool CloseOthers { get; set; } = true;
    public Guid? ReplaceEnquiryId { get; set; }
    // Every booked driver the traveller agreed to replace on these days (ReplaceEnquiryId counts too).
    public List<Guid>? ReplaceEnquiryIds { get; set; }
    // Both or neither: a journey car between two cities; Days is empty or the travel day.
    public Guid? JourneyId { get; set; }
    public int? JourneyLegIndex { get; set; }
}

public class AskAdvanceRequest
{
    public int Amount { get; set; }
    public DateTime HoldUntil { get; set; }
    public string UpiId { get; set; } = string.Empty;
    public string WhatsAppNumber { get; set; } = string.Empty;
}

public class DriverFareRequest
{
    public int Fare { get; set; }
    public string? Note { get; set; }
}

// Query of GET /trips/leads/driver; bound by the handler from query params.
public class TripDriverListQuery
{
    public string Status { get; set; } = TripDriverBuckets.New;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

// Shared by the create and edit phone-booking bodies (one validator base).
public interface IPhoneBookingFields
{
    string? HostName { get; }
    string? HostPhone { get; }
    string? RoomName { get; }
    int AgreedTotal { get; }
    bool AdvancePaid { get; }
    int? AdvanceAmount { get; }
    string? Note { get; }
}

public class CreatePhoneBookingRequest : IPhoneBookingFields
{
    public Guid StayId { get; set; }
    public int Day { get; set; }
    public bool AllNights { get; set; }
    // Explicit nights from Day (capped at the trip's last night); when set, AllNights is ignored.
    public int? Nights { get; set; }
    public string? HostName { get; set; }
    public string? HostPhone { get; set; }
    public string? RoomName { get; set; }
    public int AgreedTotal { get; set; }
    public bool AdvancePaid { get; set; }
    public int? AdvanceAmount { get; set; }
    public string? Note { get; set; }
    public bool CloseOthers { get; set; } = true;
    public Guid? ReplaceEnquiryId { get; set; }
}

public class EditPhoneBookingRequest : IPhoneBookingFields
{
    public bool AllNights { get; set; }
    public int? Nights { get; set; }
    public string? HostName { get; set; }
    public string? HostPhone { get; set; }
    public string? RoomName { get; set; }
    public int AgreedTotal { get; set; }
    public bool AdvancePaid { get; set; }
    public int? AdvanceAmount { get; set; }
    public string? Note { get; set; }
    public Guid? ReplaceEnquiryId { get; set; }
}

public class CancelTripEnquiryRequest
{
    public string? Reason { get; set; }
}

public class AcceptTripOfferRequest
{
    public bool CloseOthers { get; set; } = true;
    public Guid? ReplaceEnquiryId { get; set; }
}

// Shared by the offer and revised-offer bodies (one validator base).
public interface ITripOfferFields
{
    DateOnly CheckIn { get; }
    DateOnly CheckOut { get; }
    List<TripOfferLine> Lines { get; }
    int? AdvanceAmount { get; }
    string? BalanceNote { get; }
    string? CancellationText { get; }
    string? Inclusions { get; }
}

public class SendTripOfferRequest : ITripOfferFields
{
    public DateOnly CheckIn { get; set; }
    public DateOnly CheckOut { get; set; }
    public List<TripOfferLine> Lines { get; set; } = new();
    public int? AdvanceAmount { get; set; }
    public string? BalanceNote { get; set; }
    public string? CancellationText { get; set; }
    public bool? GstInvoice { get; set; }
    public string? Inclusions { get; set; }
    // Set when the host answers a counter with "Accept price".
    public Guid? CounterOfferId { get; set; }
}

// POST /trips/enquiries/{id}/counter (traveller).
public class CounterRequest
{
    public int PricePerNight { get; set; }
    public string? Note { get; set; }
}

// POST /trips/enquiries/{id}/revised-offer (host). ChangeRequestId = "Send different offer" on a change request.
public class RevisedOfferRequest : ITripOfferFields
{
    public DateOnly CheckIn { get; set; }
    public DateOnly CheckOut { get; set; }
    public List<TripOfferLine> Lines { get; set; } = new();
    public int? AdvanceAmount { get; set; }
    public string? BalanceNote { get; set; }
    public string? CancellationText { get; set; }
    public bool? GstInvoice { get; set; }
    public string? Inclusions { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? ChangeRequestId { get; set; }
}

// POST /trips/enquiries/{id}/change-request (traveller).
public class ChangeRequestRequest
{
    public DateOnly CheckIn { get; set; }
    public DateOnly CheckOut { get; set; }
    public int Adults { get; set; }
    public int Kids { get; set; }
    public int Rooms { get; set; }
    public string? Note { get; set; }
}

public class HostConfirmRequest
{
    public bool Confirm { get; set; }
}

// Query of GET /trips/enquiries/owner; bound by the handler from query params.
public class TripOwnerListQuery
{
    public string Status { get; set; } = TripOwnerBuckets.New;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
