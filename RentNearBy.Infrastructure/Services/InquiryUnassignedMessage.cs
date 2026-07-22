namespace RentNearBy.Infrastructure.Services;

public class InquiryUnassignedMessage
{
    public Guid InquiryId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string ConsumerName { get; set; } = string.Empty;
}
