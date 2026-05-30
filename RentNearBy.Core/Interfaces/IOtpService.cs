namespace RentNearBy.Core.Interfaces;

public interface IOtpService
{
    Task<bool> SendOtpAsync(string phoneNumber, string keyNamespace = "user");
    Task<bool> VerifyOtpAsync(string phoneNumber, string otp, string keyNamespace = "user");
}
