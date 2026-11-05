using RentNearBy.Core.Models;

namespace RentNearBy.Core.DTOs.Requests;

public class PlanTripRequest
{
    public long CityKey { get; set; }
    public string Mode { get; set; } = TripPlanModes.Auto;
    public List<PlanSpotInput>? Spots { get; set; }
    public List<PlanDayInput>? Days { get; set; }
    public int? DayLimitMinutes { get; set; }
    public DateOnly? StartDate { get; set; }
    // Auto mode only: 1..14, null = the configured default.
    public int? MaxDays { get; set; }
    // Road minutes already spent on day 1 (travel day of a city leg), 0..900.
    public int? FirstDayUsedMinutes { get; set; }
    // City leg 1..4, log only.
    public int? LegIndex { get; set; }
}

public class PlanSpotInput
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Category { get; set; } = string.Empty;
    public int VisitMinutes { get; set; }
    public int[]? ClosedDays { get; set; }
}

public class PlanDayInput
{
    public int Day { get; set; }
    public List<PlanSpotInput> Spots { get; set; } = new();
}

public class SaveTripRequest
{
    public string? Title { get; set; }
    public string? Status { get; set; }
    public TripPlan Plan { get; set; } = null!;
    // Create only; ignored by update.
    public Guid? ClientTripId { get; set; }
}
