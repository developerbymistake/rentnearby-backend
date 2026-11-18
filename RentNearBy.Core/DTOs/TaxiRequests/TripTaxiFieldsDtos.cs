namespace RentNearBy.Core.DTOs.TaxiRequests;

// Contract sections 3 and 4: the trip detail additions and the invite endpoint.

public class TripNameFieldsDto
{
    public string Title { get; set; } = string.Empty;
    public bool NameChosen { get; set; }
}

public class TripInviteDto
{
    public Guid TripId { get; set; }
    public Guid InviteCode { get; set; }
    public string? InviteLink { get; set; }
}

public class TripPlanLockDto
{
    public bool Locked { get; set; }
    public string? Reason { get; set; }
    public Guid? RequestId { get; set; }
}

public class TripTaxiRequestRefDto
{
    public Guid RequestId { get; set; }
    public string State { get; set; } = string.Empty;
    public string View { get; set; } = string.Empty;
    public long Version { get; set; }
}

public class TripTaxiFieldsDto
{
    public TripPlanLockDto PlanLock { get; set; } = new();
    public TripTaxiRequestRefDto? TaxiRequest { get; set; }
}
