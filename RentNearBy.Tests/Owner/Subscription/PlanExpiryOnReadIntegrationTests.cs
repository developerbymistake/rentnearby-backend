using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Owner.Be2;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Subscription;

// Matrix L6 "on the first request" and the ranking keys on a real row: an admin grant (non-Play) whose EndsAt passed is claimed
// by the owner's first read, not 5 minutes later. Needs RENTNEARBY_TEST_DB (PostGIS); WRITTEN, NOT RUN on the build machine.
public class PlanExpiryOnReadIntegrationTests(TestDatabase db) : StayLifecycleDbBase(db)
{
    private sealed class CountingInvalidator : IListingCacheInvalidator
    {
        public int Busts { get; private set; }
        public Task BustForUserAsync(Guid userId, string kind) { Busts++; return Task.CompletedTask; }
        public Task BustForUsersAsync(IReadOnlyCollection<Guid> userIds, string kind) { Busts++; return Task.CompletedTask; }
        public Task BustDistrictsAsync(string kind, IEnumerable<Guid> districtIds) => Task.CompletedTask;
    }

    private static PlanSlotService Slots(ApplicationDbContext c, RecordingNotifier notifier)
        => new(c, Entitlements(c, true), new FixedPlanCatalog(OwnerPlanCatalog.Seeded()), new FixedPaymentFeature(PaymentFeatureStates.On()),
            Reconcile(c, true, notifier));

    private static PlanExpiryOnRead Expiry(ApplicationDbContext c, RecordingPublisher publisher, CountingInvalidator invalidator)
        => new(c, Slots(c, new RecordingNotifier()), new FixedPlanCatalog(OwnerPlanCatalog.Seeded()), publisher, invalidator,
            NullLogger<PlanExpiryOnRead>.Instance);

    [IntegrationFact]
    public async Task First_read_after_an_admin_grant_ends_hides_extra_rooms_resets_rank_and_pushes_once()
    {
        OwnerSeed.OwnerRow owner;
        List<StayRoom> rooms;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            rooms = await OwnerSeed.RoomsAsync(c, owner.ListingId, visible: 6);
        }
        Guid grant;
        await using (var c = CreateContext())
            grant = await OwnerSeed.ActivePlanAsync(c, owner.UserId, ListingKinds.Stay, OwnerPlanCatalog.StayPlusId, UserPlanSources.AdminGrant);

        PlanLimits plus, free;
        await using (var c = CreateContext())
        {
            plus = await Entitlements(c, true).GetLimitsAsync(owner.UserId, ListingKinds.Stay);
            await using var tx = await c.Database.BeginTransactionAsync();
            var slots = Slots(c, new RecordingNotifier());
            await slots.LockOwnerAsync(owner.UserId);
            await slots.ApplyGrantEffectsAsync(owner.UserId, ListingKinds.Stay);
            await tx.CommitAsync();
        }

        // L3: the grant writes the Plus ranking keys on the real listing row.
        var granted = await ListingAsync(owner.ListingId);
        Assert.Equal((plus.PriorityRank, plus.IsFeatured), (granted.PriorityRank, granted.IsFeatured));

        // L6: EndsAt passes, status still Active. The first owner read claims it.
        await SetPlanAsync(grant, UserPlanStatuses.Active, endsInDays: -1);
        var publisher = new RecordingPublisher();
        var invalidator = new CountingInvalidator();
        await using (var c = CreateContext())
            Assert.Equal(1, await Expiry(c, publisher, invalidator).ExpireDueAsync(owner.UserId));

        await using (var c = CreateContext())
        {
            free = await Entitlements(c, true).GetLimitsAsync(owner.UserId, ListingKinds.Stay);
            Assert.Equal(UserPlanStatuses.Expired, (await c.UserPlans.AsNoTracking().SingleAsync(u => u.Id == grant)).Status);
            Assert.Single(await c.NotificationEvents.AsNoTracking()
                .Where(n => n.TargetUserId == owner.UserId && n.Type == NotificationTypes.PlanExpired).ToListAsync());
        }
        Assert.NotEqual(plus.PriorityRank, free.PriorityRank);
        var ended = await ListingAsync(owner.ListingId);
        Assert.Equal((free.PriorityRank, free.IsFeatured), (ended.PriorityRank, ended.IsFeatured));

        var now = await RoomsOfAsync(owner.ListingId);
        Assert.Equal(6, now.Count);
        Assert.Equal([rooms[4].Id, rooms[5].Id], now.Where(r => !r.HiddenByPlan).OrderBy(r => r.RoomIndex).Select(r => r.Id).ToList());
        Assert.Single(publisher.Published);
        Assert.Equal(1, invalidator.Busts);

        // A second read (or the 5-minute pass after it) finds nothing to claim: no second push.
        await using (var c = CreateContext())
            Assert.Equal(0, await Expiry(c, publisher, invalidator).ExpireDueAsync(owner.UserId));
        Assert.Single(publisher.Published);
    }

    private async Task<StayListing> ListingAsync(Guid id)
    {
        await using var c = CreateContext();
        return await c.StayListings.AsNoTracking().SingleAsync(s => s.Id == id);
    }
}

// Play-owned rows are expired by the Play sync (or its 72 h net), never by the read.
public class PlanExpiryOnReadSqlTests
{
    [Fact]
    public void The_read_claim_skips_play_owned_rows_and_ends_by_the_database_clock()
    {
        foreach (var sql in new[] { PlanExpiryOnRead.DueSql, PlanExpiryOnRead.ClaimSql })
        {
            Assert.Contains("\"PlaySubscriptionId\" IS NULL", sql);
            Assert.Contains("\"EndsAt\" <= now()", sql);
            Assert.Contains("\"Status\" = 'Active'", sql);
        }
    }
}
