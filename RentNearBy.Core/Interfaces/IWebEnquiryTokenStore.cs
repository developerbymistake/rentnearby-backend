namespace RentNearBy.Core.Interfaces;

// Backs the 3-step web-enquiry flow's per-step opaque tokens (captcha-passed -> confirmed -> otp-verified).
// Mirrors IOtpStore's exact shape (Redis + in-memory fallback, atomic get-and-delete for single-use
// enforcement) — same dual-backend convention, just storing an opaque JSON state blob under a caller-chosen
// key instead of a phone-keyed OTP string.
public interface IWebEnquiryTokenStore
{
    Task SaveAsync(string key, string value, TimeSpan ttl);

    /// <summary>
    /// Returns the stored value and atomically deletes it (single-use — a token can never be replayed
    /// against the same step twice). Returns null if expired, already consumed, or never existed.
    /// </summary>
    Task<string?> GetAndDeleteAsync(string key);
}
