using RentNearBy.Core.Interfaces.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;

// The worker side of the wake-up: Reset is called before the peek, so a Wake that lands between the peek and the wait stays set.
public interface IDueActionsWaiter
{
    void Reset();
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct);
}

// Singleton. A burst of wakes collapses to one; the timed sleep (at most 10 s) is the correctness path.
public sealed class DueActionsSignal : IDueActionsSignal, IDueActionsWaiter
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    public void Reset() => _wake.Wait(0);

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
}
