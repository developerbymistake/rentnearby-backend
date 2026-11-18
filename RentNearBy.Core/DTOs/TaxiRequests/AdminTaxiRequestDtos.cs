namespace RentNearBy.Core.DTOs.TaxiRequests;

// Contract section 11 (admin). The invite code is never part of any admin payload.

public class AdminTaxiOfferCountsDto
{
    public int Total { get; set; }
    public int Ringing { get; set; }
    public int Accepted { get; set; }
    public int Missed { get; set; }
}

public class AdminTaxiRequestItemDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string TripName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? EndReason { get; set; }
    public string TravellerName { get; set; } = string.Empty;
    public string? TravellerPhone { get; set; }
    public string? PickupName { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public AdminTaxiOfferCountsDto Offers { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public int AgeMinutes { get; set; }
}

public class AdminTaxiRequestListDto
{
    public string? NextCursor { get; set; }
    public List<AdminTaxiRequestItemDto> Items { get; set; } = new();
}

public class AdminTaxiOfferDto
{
    public Guid OfferId { get; set; }
    public Guid DriverListingId { get; set; }
    public string DriverName { get; set; } = string.Empty;
    public string? DriverPhone { get; set; }
    public string State { get; set; } = string.Empty;
    public string? EndReason { get; set; }
    public DateTime OfferedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? PushReceivedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
}

public class AdminTaxiDueActionDto
{
    public string Kind { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTime DueAt { get; set; }
    public int Attempts { get; set; }
}

public class AdminTaxiBookingItemDto
{
    public Guid BookingId { get; set; }
    public Guid RequestId { get; set; }
    public string State { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string? DriverPhone { get; set; }
    public string TravellerName { get; set; } = string.Empty;
    public string? TravellerPhone { get; set; }
    public string? PickupName { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancelChip { get; set; }
    public int? Stars { get; set; }
    public DateTime BookedAt { get; set; }
}

public class AdminTaxiBookingListDto
{
    public string? NextCursor { get; set; }
    public List<AdminTaxiBookingItemDto> Items { get; set; } = new();
}

public class AdminTaxiRequestDetailDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string TripName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? EndReason { get; set; }
    public string TravellerName { get; set; } = string.Empty;
    public string? TravellerPhone { get; set; }
    public string? PickupName { get; set; }
    public string? DropName { get; set; }
    public TaxiSectionsDto Sections { get; set; } = new();
    public int Seats { get; set; }
    public int Travellers { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool CanCancel { get; set; }
    public string? AdminNote { get; set; }
    public List<AdminTaxiOfferDto> Offers { get; set; } = new();
    public AdminTaxiBookingItemDto? Booking { get; set; }
    public List<AdminTaxiDueActionDto> DueActions { get; set; } = new();
}

public class AdminCancelTaxiRequestDto
{
    public string Note { get; set; } = string.Empty;
}

public class AdminTaxiByStateDto
{
    public int Open { get; set; }
    public int Collecting { get; set; }
    public int Selected { get; set; }
    public int Booked { get; set; }
    public int InProgress { get; set; }
    public int Completed { get; set; }
    public int Cancelled { get; set; }
    public int Expired { get; set; }
}

public class AdminTaxiHeartbeatsDto
{
    public int? DueActions { get; set; }
    public int? Matcher { get; set; }
    public int? OutboxRelay { get; set; }
}

public class AdminTaxiHealthDto
{
    public bool SwitchOn { get; set; }
    public int RequestsToday { get; set; }
    public AdminTaxiByStateDto ByState { get; set; } = new();
    public int StuckRequests { get; set; }
    public int PendingDueActions { get; set; }
    public int OldestDueActionSeconds { get; set; }
    public int OutboxPending { get; set; }
    public int OldestOutboxSeconds { get; set; }
    public int RingsSentLastHour { get; set; }
    public int RingsAckedLastHour { get; set; }
    public double? AckRatioLastHour { get; set; }
    public double? AckP95SecondsLastHour { get; set; }
    public int DriversRingReady { get; set; }
    public int DriversMissingRings { get; set; }
    public int BookedThisWeek { get; set; }
    public int? OffersAnsweredPct7d { get; set; }
    public int RoutesWithoutCity { get; set; }
    public AdminTaxiHeartbeatsDto WorkerHeartbeatSeconds { get; set; } = new();
}
