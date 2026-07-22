namespace RentNearBy.Infrastructure.Services;

public class EscalationFiledMessage
{
    public Guid InquiryId { get; set; }
    public string ConsumerName { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
