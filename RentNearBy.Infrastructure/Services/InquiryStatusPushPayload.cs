namespace RentNearBy.Infrastructure.Services;

public record InquiryStatusPushPayload(Guid RecipientUserId, Guid InquiryId, string ServiceName, string Status);
