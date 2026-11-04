namespace RentNearBy.Core.DTOs.Requests;

// POST /trips/{tripId}/problems.
public class TripProblemRequest
{
    public int Day { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid? EnquiryId { get; set; }
    public string? Note { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

// PUT /trips/{tripId}/mute.
public class MuteTripRequest
{
    public bool Muted { get; set; }
}

// POST /trips/enquiries/{id}/report-traveller (owner side).
public class ReportTravellerRequest
{
    public Guid ReasonId { get; set; }
    public string? Details { get; set; }
}
