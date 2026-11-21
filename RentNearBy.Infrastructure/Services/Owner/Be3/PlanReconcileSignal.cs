using RentNearBy.Core.Interfaces.Owner;

namespace RentNearBy.Infrastructure.Services.Owner.Be3;

// Singleton (shape of IOutboxSignal): the payment switch asks for the limits reconcile; PlanLifecycleService waits on it.
// Requests made while no one waits are kept (one pending flag), so a flip during a running pass is never lost.
public sealed class PlanReconcileSignal : IPlanReconcileSignal
{
    private readonly SemaphoreSlim _gate = new(0, 1);

    public void Request()
    {
        try
        {
            _gate.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _gate.WaitAsync(timeout, ct);
}
