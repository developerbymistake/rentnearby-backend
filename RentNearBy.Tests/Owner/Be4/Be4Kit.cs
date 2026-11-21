using RentNearBy.Core.Interfaces;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be4;

internal sealed class Be4Clock(DateTime utc) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}

internal sealed class Be4Payment(bool enabled) : IPaymentFeatureReader
{
    public Task<PaymentFeatureState> GetAsync() => Task.FromResult(new PaymentFeatureState(enabled, 30, null, PaymentProviders.Play));

    public void Invalidate()
    {
    }
}

internal sealed class Be4Catalog(PlanCatalogSnapshot? snapshot = null) : IPlanCatalogCache
{
    private readonly PlanCatalogSnapshot _snapshot = snapshot ?? OwnerPlanCatalog.Seeded();

    public Task<PlanCatalogSnapshot> GetAsync() => Task.FromResult(_snapshot);

    public Task<PlanCatalogSnapshot> RefreshAsync() => Task.FromResult(_snapshot);

    public void Invalidate()
    {
    }
}

internal static class Be4Facts
{
    public static readonly DateTime Now = new(2026, 11, 21, 6, 0, 0, DateTimeKind.Utc);
    public static readonly DateOnly Today = new(2026, 11, 21);
}
