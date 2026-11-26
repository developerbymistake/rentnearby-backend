using RentNearBy.Core.Interfaces.Buddy;

namespace RentNearBy.Tests.Support.Buddy;

public sealed class RecordingLiveHints : IBuddyLiveHints
{
    public List<(Guid TripId, IReadOnlyCollection<Guid> UserIds)> TripViewChanged { get; } = [];
    public List<(Guid TripId, long BalancesVersion, IReadOnlyCollection<Guid> UserIds)> TripExpensesChanged { get; } = [];
    public List<IReadOnlyCollection<Guid>> BuddiesChanged { get; } = [];
    public List<(string Scope, IReadOnlyCollection<Guid> UserIds)> SocialChanged { get; } = [];

    public Task TripViewChangedAsync(Guid tripId, IReadOnlyCollection<Guid> userIds)
    {
        TripViewChanged.Add((tripId, userIds.ToList()));
        return Task.CompletedTask;
    }

    public Task TripExpensesChangedAsync(Guid tripId, long balancesVersion, IReadOnlyCollection<Guid> userIds)
    {
        TripExpensesChanged.Add((tripId, balancesVersion, userIds.ToList()));
        return Task.CompletedTask;
    }

    public Task BuddiesChangedAsync(IReadOnlyCollection<Guid> userIds)
    {
        BuddiesChanged.Add(userIds.ToList());
        return Task.CompletedTask;
    }

    public Task SocialChangedAsync(string scope, IReadOnlyCollection<Guid> userIds)
    {
        SocialChanged.Add((scope, userIds.ToList()));
        return Task.CompletedTask;
    }
}
