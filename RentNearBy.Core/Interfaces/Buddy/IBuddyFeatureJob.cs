namespace RentNearBy.Core.Interfaces.Buddy;

public interface IBuddyFeatureJob
{
    string Name { get; }

    TimeSpan Interval { get; }

    // Set = once per IST day at or after this time (Interval ignored).
    TimeOnly? DailyAtIst { get; }

    // Rows handled; idempotent; batched inside.
    Task<int> RunAsync(CancellationToken ct);
}
