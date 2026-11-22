namespace RentNearBy.Core.Models.Owner;

// Attempt (FB-52): a second bucket taken first and never given back, so refused writes (given back from the write bucket)
// are still throttled.
public sealed record OwnerRateLimit(string Key, int Max, TimeSpan Window, OwnerRateLimit? Attempt = null);

// Contract 2.0: reads 60 a minute, writes 30 a minute per user (the numbers of TaxiRateLimits). One bucket per surface.
public static class OwnerRateLimitKeys
{
    public const int ReadsPerMinute = 60;
    public const int WritesPerMinute = 30;
    public const int AttemptsPerMinute = 120;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    public static OwnerRateLimit Read(Guid userId) => new($"owner:read:{userId}", ReadsPerMinute, Minute);
    public static OwnerRateLimit Write(Guid userId)
        => new($"owner:write:{userId}", WritesPerMinute, Minute, new OwnerRateLimit($"owner:attempt:{userId}", AttemptsPerMinute, Minute));
    public static OwnerRateLimit Photo(Guid userId) => new($"owner:photo:{userId}", 20, TimeSpan.FromHours(1));
    public static OwnerRateLimit HomeRead(Guid userId) => new($"owner:home:{userId}", ReadsPerMinute, Minute);
    public static OwnerRateLimit SeekerFeedRead(Guid userId) => new($"home:seeker:{userId}", ReadsPerMinute, Minute);
    public static OwnerRateLimit FestivalRead(Guid userId) => new($"festival:read:{userId}", ReadsPerMinute, Minute);
    // Profile picture: any role, 10 an hour, never given back (each one costs a Cloudinary upload).
    public static OwnerRateLimit ProfilePhoto(Guid userId) => new($"user:photo:{userId}", 10, TimeSpan.FromHours(1));
}
