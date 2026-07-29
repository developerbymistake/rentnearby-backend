namespace RentNearBy.Infrastructure.Services;

public class EnquiryUnassignedMessage
{
    public Guid EnquiryId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string ConsumerName { get; set; } = string.Empty;
}
