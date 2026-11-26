namespace RentNearBy.Core.DTOs.Requests;

public class PhoneOnboardingRequest
{
    public string PhoneNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    // The username verify-otp returned to the caller who passed the OTP; proves this caller verified the phone.
    public string? Username { get; set; }

    // Bakhli social onboarding (optional): all of Gender..About present = the profile row is written with the account;
    // under 18 = 400 Under18 and no account. None present = the old behaviour.
    public string? Gender { get; set; }
    public int? BirthMonth { get; set; }
    public int? BirthYear { get; set; }
    public List<string>? Interests { get; set; }
    public List<string>? Skills { get; set; }
    public List<string>? Places { get; set; }
    public string? About { get; set; }
    public string? HomeCityName { get; set; }
    public string? HomeStateName { get; set; }
}
