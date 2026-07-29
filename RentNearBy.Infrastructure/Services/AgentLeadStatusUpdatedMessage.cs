namespace RentNearBy.Infrastructure.Services;

public class AgentLeadStatusUpdatedMessage
{
    public Guid EnquiryId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
