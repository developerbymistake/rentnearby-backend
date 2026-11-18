using System.Collections.Concurrent;
using RentNearBy.Core.Interfaces.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.Matching;

// Singleton. Dirty cities are a set, so a burst of marks for one city collapses to one pending pass. Freed drivers are
// queued and turned into cities by the service before its next drain. Reset is called before the service takes the set,
// so a mark that lands while a drain runs keeps the wake set and is never lost.
public sealed class MatcherSignal : IMatcherSignal
{
    private readonly ConcurrentDictionary<long, byte> _dirty = new();
    private readonly ConcurrentQueue<Guid> _freedDrivers = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void MarkDirty(long cityKey)
    {
        _dirty.TryAdd(cityKey, 0);
        Wake();
    }

    public void MarkDriverFreed(Guid driverListingId)
    {
        _freedDrivers.Enqueue(driverListingId);
        Wake();
    }

    public int PendingCities => _dirty.Count;

    public bool IsDirty(long cityKey) => _dirty.ContainsKey(cityKey);

    // Removes and returns every dirty city except the ones the caller holds back.
    public IReadOnlyList<long> TakeDirty(Func<long, bool>? holdBack = null)
    {
        var taken = new List<long>();
        foreach (var city in _dirty.Keys)
        {
            if (holdBack?.Invoke(city) == true) continue;
            if (_dirty.TryRemove(city, out _)) taken.Add(city);
        }
        taken.Sort();
        return taken;
    }

    // True when the city was marked again since it was taken; the mark is consumed by the caller's rerun.
    public bool TryTake(long cityKey) => _dirty.TryRemove(cityKey, out _);

    public IReadOnlyList<Guid> TakeFreedDrivers()
    {
        var ids = new HashSet<Guid>();
        while (_freedDrivers.TryDequeue(out var id)) ids.Add(id);
        return ids.ToList();
    }

    public void Reset() => _wake.Wait(0);

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }
}
