namespace RentNearBy.Core.DTOs.Requests;

// UserId comes from the authenticated ClaimsPrincipal, not this body (matches CreateListingRequest's
// convention). AgreedToTerms is a request-time consent flag only — it is NOT persisted on the Inquiry
// entity (no such column), it just gates submission.
public record CreateInquiryRequest(
    Guid ServiceId, Guid ServicePackageId, string FullName, string Mobile, string? Email,
    DateTime? PreferredDateOrTripStart, int? NumberOfPeople, string? Message, bool AgreedToTerms);
