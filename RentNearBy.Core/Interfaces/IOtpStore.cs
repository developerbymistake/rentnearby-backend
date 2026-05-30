namespace RentNearBy.Core.Interfaces;

public interface IOtpStore
{
    Task SaveAsync(string phoneNumber, string otp, TimeSpan ttl);

    /// <summary>
    /// Returns the stored OTP and atomically deletes it (one-time use).
    /// Returns null if expired or not found.
    /// </summary>
    Task<string?> GetAndDeleteAsync(string phoneNumber);
}
