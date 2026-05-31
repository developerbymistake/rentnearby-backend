namespace RentNearBy.Core.DTOs.Requests;

public class PhoneLoginVerifyOtpRequest
{
    public string PhoneNumber { get; set; } = string.Empty;
    public string Otp { get; set; } = string.Empty;
}
