namespace RentNearBy.Core.Interfaces.Owner;

// Singleton (shape of IOutboxSignal). PUT /admin/payment-feature calls Request() after its commit when isEnabled flips;
// PlanLifecycleService waits on it and runs the limits reconcile at once (contract 3.4). Lane BE3.
public interface IPlanReconcileSignal
{
    void Request();

    // true = a request arrived before the timeout.
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct);
}
