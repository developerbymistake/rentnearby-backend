namespace RentNearBy.Infrastructure.Services;

public class ReportFiledMessage
{
    public Guid OwnerId { get; set; }
    public string ReasonName { get; set; } = string.Empty;
    public string ListingTitle { get; set; } = string.Empty;
}
