namespace RentNearBy.Core.DTOs.Responses;

public class GoogleSignInResponse
{
    public bool NeedsOnboarding { get; set; }
    public string? Token { get; set; }
    public UserDto? User { get; set; }
    public GoogleProfileDto? GoogleProfile { get; set; }
}

public class GoogleProfileDto
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
}
