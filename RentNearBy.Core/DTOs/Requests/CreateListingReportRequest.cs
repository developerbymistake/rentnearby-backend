namespace RentNearBy.Core.DTOs.Requests;

public class CreateListingReportRequest
{
    public Guid ReasonId { get; set; }
    public string Details { get; set; } = string.Empty;
}
