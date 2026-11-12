namespace RentNearBy.Core.DTOs.Requests;

public class AskLegRequest
{
    public int LegIndex { get; set; }
    public string LegKey { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string ToName { get; set; } = string.Empty;
    // Ignored: the lead's listed rate is priced on the server (JourneyLegFares, JourneyLegPricing.CarsFor).
    public int? ListedFare { get; set; }
    public List<Guid> TaxiIds { get; set; } = new();
    public int CarOf { get; set; } = 1;
    public string? ComesFrom { get; set; }
}

public class AskDriversRequest
{
    public DateOnly Date { get; set; }
    public int People { get; set; }
    public Guid JourneyId { get; set; }
    public string FromName { get; set; } = string.Empty;
    public string ToName { get; set; } = string.Empty;
    public List<AskLegRequest> Legs { get; set; } = new();
    public Guid? ClientRequestId { get; set; }
    public string? StepKey { get; set; }
}

public class SaveJourneyLegRequest
{
    public int LegIndex { get; set; }
    public string LegKey { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public double FromLat { get; set; }
    public double FromLng { get; set; }
    public string ToName { get; set; } = string.Empty;
    public double ToLat { get; set; }
    public double ToLng { get; set; }
    public string? MeetingPlace { get; set; }
    public Guid DriverTaxiId { get; set; }
    public Guid? DriverRouteId { get; set; }
    public int? ListedFare { get; set; }
    public bool Partial { get; set; }
    public Guid? Backup1TaxiId { get; set; }
    public Guid? Backup2TaxiId { get; set; }
    // The other cars of the leg's seat cover the traveller accepted (WayLegDto.CoverTaxiIds minus the driver). The server
    // re-solves the cover over the driver and these with its own fares; ListedFare is ignored.
    public List<Guid> CoverTaxiIds { get; set; } = new();
}

public class SaveJourneyRequest
{
    public string FromName { get; set; } = string.Empty;
    public double FromLat { get; set; }
    public double FromLng { get; set; }
    public string ToName { get; set; } = string.Empty;
    public double ToLat { get; set; }
    public double ToLng { get; set; }
    public DateOnly Date { get; set; }
    public int People { get; set; }
    public string PlanKey { get; set; } = string.Empty;
    public List<SaveJourneyLegRequest> Legs { get; set; } = new();
    // Travel between two cities of a trip: both together; LegIndex = the city leg travelled to (2..n).
    public Guid? TripId { get; set; }
    public int? LegIndex { get; set; }
}

public class ReplaceJourneyDriverRequest
{
    public Guid TaxiId { get; set; }
}

public class JourneyStatusRequest
{
    public string Status { get; set; } = string.Empty;
}
