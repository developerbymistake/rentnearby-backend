using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be2;
using RentNearBy.Tests.Support;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be2;

public sealed class FixedPlanCatalog(PlanCatalogSnapshot snapshot) : IPlanCatalogCache
{
    public Task<PlanCatalogSnapshot> GetAsync() => Task.FromResult(snapshot);
    public Task<PlanCatalogSnapshot> RefreshAsync() => Task.FromResult(snapshot);
    public void Invalidate() { }
}

public sealed class FixedPaymentFeature(PaymentFeatureState state) : IPaymentFeatureReader
{
    public Task<PaymentFeatureState> GetAsync() => Task.FromResult(state);
    public void Invalidate() { }
}

public sealed class RecordingOwnerNotifier : IOwnerNotifier
{
    public List<(string Kind, Guid EntityId, Guid[] Users)> Cleared { get; } = [];

    public Task StoreAsync(IReadOnlyCollection<NoticeUpsert> notices, CancellationToken ct = default) => Task.CompletedTask;

    public Task ClearAsync(string kind, Guid entityId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        Cleared.Add((kind, entityId, userIds.ToArray()));
        return Task.CompletedTask;
    }

    public Task PushAsync(IReadOnlyCollection<OwnerPush> pushes, CancellationToken ct = default) => Task.CompletedTask;

    public void Wake() { }
}

// Builds the lane BE2 services on one context. Payment ON = Free numbers for an owner without a plan (1 special price).
public sealed class Be2Kit(ApplicationDbContext db, bool paymentOn = true, FestivalCache? festivals = null)
{
    public RecordingOwnerNotifier Notifier { get; } = new();
    public FestivalCache Festivals { get; } = festivals ?? new FestivalCache(new MemoryCache(new MemoryCacheOptions()));

    public OwnerContextReader Contexts => new(db, new FixedPlanCatalog(OwnerPlanCatalog.Seeded()),
        new FixedPaymentFeature(paymentOn ? PaymentFeatureStates.On() : PaymentFeatureStates.Off()), TimeProvider.System);

    public StaySpecialPriceService Prices => new(db, Contexts, Festivals, Notifier, NullLogger<StaySpecialPriceService>.Instance);

    public FestivalService FestivalAdmin => new(db, Festivals, TimeProvider.System);

    public static DateOnly Today => IstTime.TodayIst(DateTime.UtcNow);
}
