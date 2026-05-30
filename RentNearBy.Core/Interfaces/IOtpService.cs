namespace RentNearBy.Core.Interfaces;

public interface IOtpService
{
    Task<bool> SendOtpAsync(string phoneNumber);
    Task<bool> VerifyOtpAsync(string phoneNumber, string otp);
}
