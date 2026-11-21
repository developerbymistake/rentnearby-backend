namespace RentNearBy.Core.Models.Owner;

public sealed record OwnerRateLimit(string Key, int Max, TimeSpan Window);

// Contract 2.0: reads 60 a minute, writes 30 a minute per user (the numbers of TaxiRateLimits). One bucket per surface.
public static class OwnerRateLimitKeys
{
    public const int ReadsPerMinute = 60;
    public const int WritesPerMinute = 30;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    public static OwnerRateLimit Read(Guid userId) => new($"owner:read:{userId}", ReadsPerMinute, Minute);
    public static OwnerRateLimit Write(Guid userId) => new($"owner:write:{userId}", WritesPerMinute, Minute);
    public static OwnerRateLimit HomeRead(Guid userId) => new($"owner:home:{userId}", ReadsPerMinute, Minute);
    public static OwnerRateLimit SeekerFeedRead(Guid userId) => new($"home:seeker:{userId}", ReadsPerMinute, Minute);
    public static OwnerRateLimit FestivalRead(Guid userId) => new($"festival:read:{userId}", ReadsPerMinute, Minute);
}
