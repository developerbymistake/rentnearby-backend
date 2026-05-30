namespace RentNearBy.Core.DTOs.Requests;

public class AdminResetPasswordRequest
{
    public string PhoneNumber { get; set; } = string.Empty;
    public string Otp { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}
