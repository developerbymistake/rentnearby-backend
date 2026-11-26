using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Support.Buddy;

namespace RentNearBy.Tests.Support.Social;

// Seams of the social feature for tests that build services by hand.
public sealed class FixedSeekerLimits(SeekerLimits limits) : ISeekerLimits
{
    public Task<SeekerLimits> GetAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(limits);
}

public sealed class RecordingSocialRealtime : ISocialRealtime
{
    public List<SocialHubEvent> Sent { get; } = [];

    public Task SendAsync(IReadOnlyCollection<SocialHubEvent> events)
    {
        Sent.AddRange(events);
        return Task.CompletedTask;
    }
}

public static class SocialTestSeams
{
    public static ISeekerLimits Limits(int activeTrips, bool plus = false) => new FixedSeekerLimits(plus
        ? new SeekerLimits(true, false, activeTrips, 3, 5, 15, true, true)
        : new SeekerLimits(false, true, activeTrips, 1, 1, 3, false, false));

    public static SocialAfterCommit AfterCommit(ApplicationDbContext ctx, RecordingSocialRealtime? realtime = null, RecordingLiveHints? hints = null)
        => new(ctx, realtime ?? new RecordingSocialRealtime(), hints ?? new RecordingLiveHints(), new NoticeWriter(ctx));

    public static TripGroupChat GroupChat(ApplicationDbContext ctx, SocialAfterCommit afterCommit) => new(ctx, afterCommit);
}
