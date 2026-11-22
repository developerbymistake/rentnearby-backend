namespace RentNearBy.Core.DTOs.Requests;

public class PhoneOnboardingRequest
{
    public string PhoneNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    // The username verify-otp returned to the caller who passed the OTP; proves this caller verified the phone.
    public string? Username { get; set; }
}
