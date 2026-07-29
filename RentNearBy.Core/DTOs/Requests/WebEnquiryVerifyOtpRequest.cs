namespace RentNearBy.Core.DTOs.Requests;

// Step 3 — final OTP verification. On success this creates/finds the User and the Enquiry itself (see
// WebEnquiryHandlers.VerifyOtp) — no JWT/Session is ever issued here by design (see the class doc comment
// on WebEnquiryHandlers): the website never gets a general-purpose access token, closing off the "website
// login could be used to hit unrelated mobile-app APIs" risk entirely.
public record WebEnquiryVerifyOtpRequest(string Token, string Otp);
