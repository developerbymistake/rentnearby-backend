namespace RentNearBy.Infrastructure.Services;

// ForAgent distinguishes the two structurally different recipients this same queue serves: the
// consumer who submitted the inquiry ("Your inquiry...") vs a co-assigned agent being told about a
// status change on a lead they don't own ("Your assigned lead..."). Defaulted so every pre-existing
// positional call site (all consumer-facing) is unaffected.
public record InquiryStatusPushPayload(Guid RecipientUserId, Guid InquiryId, string ServiceName, string Status, bool ForAgent = false);
