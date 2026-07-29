namespace RentNearBy.Core.DTOs.Requests;

// Step 1 of the public (unauthenticated) website enquiry flow — mirrors CreateEnquiryRequest's fields
// exactly (same enquiry data), plus TurnstileToken, the one thing that gates everything downstream. No
// UserId here at all — identity doesn't exist yet at this point in the flow, it's established only after
// Step 3's OTP verification.
public record WebEnquiryStartRequest(
    Guid ServiceId, Guid ServicePackageId, string FullName, string Mobile, string? Email,
    DateTime? PreferredDateOrTripStart, int? NumberOfPeople, string? Message, bool AgreedToTerms,
    string TurnstileToken);
