namespace RentNearBy.Core.DTOs.Responses;

public class PhoneLoginResponse
{
    public bool NeedsOnboarding { get; set; }
    public string? Token { get; set; }
    public UserDto? User { get; set; }
}
