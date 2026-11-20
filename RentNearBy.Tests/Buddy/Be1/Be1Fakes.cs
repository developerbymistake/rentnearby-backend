using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be1;
using RentNearBy.Tests.Support.Buddy;

namespace RentNearBy.Tests.Buddy.Be1;

public sealed class FakeTripClosure : ITripEnquiryClosure
{
    public int Blocks { get; private set; }

    public Task<IReadOnlyList<TripClosurePush>> OnListingsRemovedAsync(string kind, IReadOnlyCollection<Guid> listingIds) => Task.FromResult<IReadOnlyList<TripClosurePush>>([]);
    public Task<IReadOnlyList<TripClosurePush>> OnTripDeletedAsync(Guid tripId) => Task.FromResult<IReadOnlyList<TripClosurePush>>([]);
    public Task<IReadOnlyList<TripClosurePush>> OnTravellerDeletedAsync(Guid userId) => Task.FromResult<IReadOnlyList<TripClosurePush>>([]);

    public Task OnBlockAsync(Guid userA, Guid userB)
    {
        Blocks++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TripClosurePush>> OnTaxiRateClearedAsync(Guid taxiId) => Task.FromResult<IReadOnlyList<TripClosurePush>>([]);
    public Task AfterCommitAsync(IReadOnlyList<TripClosurePush> pushes) => Task.CompletedTask;
}

public sealed class FakePublisher : IRabbitMqPublisher
{
    public List<(string Queue, string Message)> Published { get; } = [];

    public Task PublishAsync(string queue, string message)
    {
        Published.Add((queue, message));
        return Task.CompletedTask;
    }
}

public sealed class RecordingBlockParticipant : IUserBlockParticipant
{
    public List<(Guid Blocker, Guid Blocked)> Calls { get; } = [];

    public Task OnBlockedAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default)
    {
        Calls.Add((blockerId, blockedId));
        return Task.CompletedTask;
    }
}

// One set of BE1 services over one context, with recording seams.
public sealed class Be1Kit(ApplicationDbContext db)
{
    public RecordingNoticeWriter Notices { get; } = new();
    public RecordingLiveHints Hints { get; } = new();
    public FakeTripClosure Closure { get; } = new();
    public FakePublisher Publisher { get; } = new();
    public RecordingBlockParticipant Participant { get; } = new();

    public BuddyFindService Find => new(db);
    public BuddyRequestService Requests => new(db, Notices, Hints, NullLogger<BuddyRequestService>.Instance);
    public BuddyListService Lists => new(db, Hints, NullLogger<BuddyListService>.Instance);
    public UserBlockService Blocks => new(db, [Participant], Closure, Hints, NullLogger<UserBlockService>.Instance);
    public UserReportService Reports => new(db, Publisher, new MemoryCache(new MemoryCacheOptions()), NullLogger<UserReportService>.Instance);
}
