using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Owner.Be2;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Subscription;

// L6 / L10 / L11-end: the real expiry path (ExpireEffectsAsync, used by the lifecycle claim, Play sync, refund and admin revoke)
// stores the PlanLimitsChanged notice itself; the caller's PlanExpired / Ended push stays the only push. Needs RENTNEARBY_TEST_DB.
public class ExpireNoticeIntegrationTests(TestDatabase db) : StayLifecycleDbBase(db)
{
    [IntegrationFact]
    public async Task Expire_effects_store_one_plan_limits_notice_and_no_second_push()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            await OwnerSeed.RoomsAsync(c, owner.ListingId, visible: 4);
        }
        var plus = await BuyPlusAsync(owner.UserId);
        Assert.False((await ApplyAsync(owner.UserId, true)).Changed);
        await SetPlanAsync(plus, UserPlanStatuses.Expired);

        var notifier = new RecordingNotifier();
        await using (var c = CreateContext())
        {
            await using var tx = await c.Database.BeginTransactionAsync();
            await c.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {owner.UserId} FOR UPDATE");
            var catalog = new FixedPlanCatalog(OwnerPlanCatalog.Seeded());
            var payment = new FixedPaymentFeature(PaymentFeatureStates.On());
            var slots = new PlanSlotService(c, Entitlements(c, true), catalog, payment, Reconcile(c, true, notifier));
            var effects = await slots.ExpireEffectsAsync(owner.UserId, ListingKinds.Stay);
            await tx.CommitAsync();
            Assert.True(effects.Paused > 0);
        }

        var stored = Assert.Single(notifier.Stored);
        Assert.Equal((owner.UserId, NoticeKinds.PlanLimitsChanged, (Guid?)owner.ListingId), (stored.UserId, stored.Kind, stored.RefId));
        Assert.Empty(notifier.Pushed);
    }
}
