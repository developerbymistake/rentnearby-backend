namespace RentNearBy.Core.Models.Owner;

// SPEC 6: two independent rolling-hour buckets per driver (user id; one live taxi per user).
public static class TaxiHourlyLimits
{
    public const int Max = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(60);

    public const string AvailabilityBucket = "availability";
    public const string BookingsBucket = "bookings";
    public const string Message = "You can change this 3 times an hour.";

    public static string Availability(Guid userId) => $"taxi:avail:write:{userId:N}";

    public static string Bookings(Guid userId) => $"taxi:cal:write:{userId:N}";

    public static string KeyOf(string bucket, Guid userId) => bucket == AvailabilityBucket ? Availability(userId) : Bookings(userId);

    // Whole seconds, 1 to 3600.
    public static int RetrySeconds(TimeSpan? retryAfter)
        => (int)Math.Clamp(Math.Ceiling((retryAfter ?? Window).TotalSeconds), 1, Window.TotalSeconds);
}
