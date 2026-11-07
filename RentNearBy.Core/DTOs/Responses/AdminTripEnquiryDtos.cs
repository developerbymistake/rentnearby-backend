using RentNearBy.Core.Models;

namespace RentNearBy.Core.DTOs.Responses;

public class AdminTripEnquiryDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = TripEnquiryKinds.Stay;
    public string Status { get; set; } = string.Empty;
    public string? FailReason { get; set; }
    public string DisplayStatus { get; set; } = string.Empty;
    public string AdvanceStatus { get; set; } = TripAdvanceStatuses.None;
    public Guid ListingId { get; set; }
    public string ListingName { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string TravellerName { get; set; } = string.Empty;
    public string? TravellerPhone { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public string? OwnerPhone { get; set; }
    public DateOnly? CheckIn { get; set; }
    public DateOnly? CheckOut { get; set; }
    public int[]? Days { get; set; }
    public int? AgreedTotal { get; set; }
    public int? AgreedFare { get; set; }
    public int? DriverFare { get; set; }
    public bool IsUrgent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AdminTripEnquiryPageDto
{
    public List<AdminTripEnquiryDto> Items { get; set; } = new();
    public bool HasMore { get; set; }
}

// The advance timeline never carries a UPI ID: the admin API does not read chat messages.
public class AdminTripAdvanceDto
{
    public string Status { get; set; } = TripAdvanceStatuses.None;
    public int? Amount { get; set; }
    public DateTime? HoldUntil { get; set; }
    public DateTime? AskedAt { get; set; }
    public DateTime? ProofSentAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public DateTime? NotYetAt { get; set; }
    public int ReminderCount { get; set; }
    public DateTime? LastHostReminderAt { get; set; }
}

public class AdminTripTaxiDto
{
    public int? ListedRate { get; set; }
    public int? DriverFare { get; set; }
    public string? DriverNote { get; set; }
    public int FareEditCount { get; set; }
    public int? AgreedFare { get; set; }
    public bool TollExtra { get; set; }
    public string? PickupTime { get; set; }
    public string? PickupPlace { get; set; }
    public int[]? Days { get; set; }
}

public class AdminTripEnquiryDetailDto : AdminTripEnquiryDto
{
    public TripEnquiryContextDto Context { get; set; } = new();
    public List<TripOfferDto> Offers { get; set; } = new();
    public AdminTripAdvanceDto Advance { get; set; } = new();
    public AdminTripTaxiDto? Taxi { get; set; }
    public Guid? TripId { get; set; }
    public Guid? ConversationId { get; set; }
    public string? ClosedNote { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
    public List<AdminTripProblemDto> Problems { get; set; } = new();
}

public class AdminTripProblemDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string TripTitle { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public int Day { get; set; }
    public DateOnly? Date { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? Note { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public Guid? EnquiryId { get; set; }
    public string? EnquiryKind { get; set; }
    public string? ListingName { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    public string TravellerName { get; set; } = string.Empty;
    public string? TravellerPhone { get; set; }
    public bool Marked { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AdminTripProblemPageDto
{
    public List<AdminTripProblemDto> Items { get; set; } = new();
    public bool HasMore { get; set; }
}
