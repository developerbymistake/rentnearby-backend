namespace RentNearBy.Infrastructure.Services.Social;

// Singleton (shape of OutboxSignal). A follow write calls Wake() after its commit; FollowEffectsJob waits on it. The 1 s poll
// of the job is the correctness path, the wake only makes the bell row and the counts appear sooner.
public interface IFollowEffectsSignal
{
    void Wake();

    // true = a wake arrived before the timeout.
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct);
}

public sealed class FollowEffectsSignal : IFollowEffectsSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
}
