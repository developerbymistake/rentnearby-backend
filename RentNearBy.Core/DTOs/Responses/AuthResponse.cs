namespace RentNearBy.Core.DTOs.Responses;

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public UserDto User { get; set; } = null!;
}
