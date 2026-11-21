using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner;

// Safe defaults registered with TryAdd* by AddOwnerFeature; each lane replaces its own in its OwnerBeNRegistration file.

public sealed class NullOwnerNotifier : IOwnerNotifier
{
    public Task StoreAsync(IReadOnlyCollection<NoticeUpsert> notices, CancellationToken ct = default) => Task.CompletedTask;

    public Task ClearAsync(string kind, Guid entityId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) => Task.CompletedTask;

    public Task PushAsync(IReadOnlyCollection<OwnerPush> pushes, CancellationToken ct = default) => Task.CompletedTask;

    public void Wake()
    {
    }
}

public sealed class NullOwnerCalendarSync : IOwnerCalendarSync
{
    private static readonly IReadOnlyList<DateOnly> NoDates = [];

    public Task SyncEnquiryAsync(ApplicationDbContext db, Guid enquiryId, CancellationToken ct = default) => Task.CompletedTask;

    public Task SyncTaxiBookingAsync(ApplicationDbContext db, Guid bookingId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<DateOnly>> ManualDatesAsync(
        ApplicationDbContext db, Guid taxiListingId, DateOnly from, DateOnly to, CancellationToken ct = default) => Task.FromResult(NoDates);
}

public sealed class NullOwnerListingPurge : IOwnerListingPurge
{
    public Task PurgeAsync(string listingKind, IReadOnlyCollection<Guid> listingIds, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class NullStayDatedPricing : IStayDatedPricing
{
    public List<StayNearbyDto> Apply(
        List<StayNearbyDto> items, DateOnly? checkIn, int nights, int? guests, int? rooms, bool groundFloorOnly, DateOnly todayIst) => items;
}

public sealed class NullStayDetailPricing : IStayDetailPricing
{
    public Task ApplyAsync(StayPublicDto dto, DateOnly? checkIn, int nights, DateOnly todayIst, CancellationToken ct = default) => Task.CompletedTask;

    public Task ApplyAsync(StayPublicSlugDto dto, DateOnly? checkIn, int nights, DateOnly todayIst, CancellationToken ct = default) => Task.CompletedTask;
}

// Never signalled: the lifecycle service keeps its daily rhythm until lane BE3 registers PlanReconcileSignal.
public sealed class NullPlanReconcileSignal : IPlanReconcileSignal
{
    public void Request()
    {
    }

    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(timeout, ct);
        return false;
    }
}
