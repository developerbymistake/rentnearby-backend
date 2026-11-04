namespace RentNearBy.Core.DTOs.Requests;

public class CreateListingReportRequest
{
    public Guid ReasonId { get; set; }
    public string Details { get; set; } = string.Empty;
    // Stay and taxi reports only: the traveller's own enquiry that gave rise to the report.
    public Guid? TripEnquiryId { get; set; }
}
