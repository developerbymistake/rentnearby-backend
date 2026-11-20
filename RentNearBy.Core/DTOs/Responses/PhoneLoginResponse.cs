namespace RentNearBy.Core.DTOs.Responses;

public class PhoneLoginResponse
{
    public bool NeedsOnboarding { get; set; }
    public string? Token { get; set; }
    // New user: the username held for this phone, shown on the profile screen before the name is sent.
    public string? Username { get; set; }
    public UserDto? User { get; set; }
}
