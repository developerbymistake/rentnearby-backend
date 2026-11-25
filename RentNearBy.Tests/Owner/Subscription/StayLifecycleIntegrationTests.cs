using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be2;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Owner.Be2;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Subscription;

// Subscription matrix (Stay owner) on a real database: rooms, photos, jobs and special prices walked through Plus, expire (by the
// clock, by status, hold, refund), re-subscribe, payment OFF and the second identical pass. The owner's plan is a real UserPlans
// row. Needs RENTNEARBY_TEST_DB (PostGIS); WRITTEN, NOT RUN on the build machine. The database is shared, so every assertion reads
// this test's own owner only.
public class StayLifecycleIntegrationTests(TestDatabase db) : StayLifecycleDbBase(db)
{
    [IntegrationFact]
    public async Task Rooms_and_photos_walk_plus_expire_resubscribe_hold_refund_and_payment_off()
    {
        OwnerSeed.OwnerRow owner;
        List<StayRoom> rooms;
        List<StayPhoto> photos;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            rooms = await OwnerSeed.RoomsAsync(c, owner.ListingId, visible: 6);
            photos = await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 8);
        }
        var plusRow = await BuyPlusAsync(owner.UserId);

        // L3 / L4: Plus holds all 6 rooms and 8 photos; the pass changes nothing.
        Assert.False((await ApplyAsync(owner.UserId, true)).Changed);

        // L6: the plan ends by the clock (status still Active, no Play id): last 2 rooms and FIRST 2 photos stay, nothing is deleted.
        await SetPlanAsync(plusRow, UserPlanStatuses.Active, endsInDays: -1);
        var expired = await ApplyAsync(owner.UserId, true);
        Assert.True(expired.Changed);
        Assert.Equal((0, 10), (expired.HiddenBefore, expired.HiddenAfter));
        await AssertExpiredAsync();

        // The second identical pass writes nothing.
        Assert.False((await ApplyAsync(owner.UserId, true)).Changed);

        // L9: a new Plus row brings everything back in the order it was added; photo order never moved.
        var plusAgain = await BuyPlusAsync(owner.UserId);
        var restored = await ApplyAsync(owner.UserId, true);
        Assert.Equal((10, 0), (restored.HiddenBefore, restored.HiddenAfter));
        var roomsBack = await RoomsOfAsync(owner.ListingId);
        Assert.Equal(rooms.Select(r => r.Id).ToList(), roomsBack.Select(r => r.Id).ToList());
        Assert.Equal([1, 2, 3, 4, 5, 6], roomsBack.Select(r => (int)r.RoomIndex).ToList());
        Assert.DoesNotContain(roomsBack, r => r.HiddenByPlan);
        Assert.DoesNotContain(await PhotosOfAsync(owner.ListingId), p => p.HiddenByPlan);

        // L8 hold: the sync expires the row and leaves EndsAt in the future. Same result as expiry.
        await SetPlanAsync(plusAgain, UserPlanStatuses.Expired);
        Assert.True((await ApplyAsync(owner.UserId, true)).Changed);
        await AssertExpiredAsync();

        // L10 refund / revoke: re-buy, then Cancelled. Same result again.
        var third = await BuyPlusAsync(owner.UserId);
        Assert.True((await ApplyAsync(owner.UserId, true)).Changed);
        Assert.DoesNotContain(await RoomsOfAsync(owner.ListingId), r => r.HiddenByPlan);
        await SetPlanAsync(third, UserPlanStatuses.Cancelled);
        Assert.True((await ApplyAsync(owner.UserId, true)).Changed);
        await AssertExpiredAsync();

        // L14: payment OFF shows everything (nothing hidden, nothing deleted); ON again hides the same rows.
        Assert.True((await ApplyAsync(owner.UserId, false)).Changed);
        Assert.DoesNotContain(await RoomsOfAsync(owner.ListingId), r => r.HiddenByPlan);
        Assert.DoesNotContain(await PhotosOfAsync(owner.ListingId), p => p.HiddenByPlan);
        Assert.False((await ApplyAsync(owner.UserId, false)).Changed);
        Assert.True((await ApplyAsync(owner.UserId, true)).Changed);
        await AssertExpiredAsync();

        async Task AssertExpiredAsync()
        {
            var now = await RoomsOfAsync(owner.ListingId);
            Assert.Equal(6, now.Count);
            var visible = now.Where(r => !r.HiddenByPlan).OrderBy(r => r.RoomIndex).ToList();
            Assert.Equal([rooms[4].Id, rooms[5].Id], visible.Select(r => r.Id).ToList());
            Assert.Equal([1, 2], visible.Select(r => (int)r.RoomIndex).ToList());
            Assert.Equal([rooms[0].Id, rooms[1].Id, rooms[2].Id, rooms[3].Id], now.Where(r => r.HiddenByPlan).OrderBy(r => r.RoomIndex).Select(r => r.Id).ToList());
            Assert.All(now.Where(r => r.HiddenByPlan), r => Assert.True(r.RoomIndex > PlanRowBands.HiddenBase));

            var shots = await PhotosOfAsync(owner.ListingId);
            Assert.Equal(8, shots.Count);
            Assert.Equal([photos[0].Id, photos[1].Id], shots.Where(p => !p.HiddenByPlan).OrderBy(p => p.PhotoOrder).Select(p => p.Id).ToList());
            Assert.Equal(photos.Select(p => p.PhotoOrder).ToList(), shots.Select(p => p.PhotoOrder).ToList());
            await using var c = CreateContext();
            Assert.Equal(rooms[4].PricePerNight, (await c.StayListings.AsNoTracking().SingleAsync(s => s.Id == owner.ListingId)).MinPricePerNight);
        }
    }

    [IntegrationFact]
    public async Task Jobs_and_special_prices_daily_pass_expire_one_notice_second_pass_silent_resubscribe_clears_and_off_restores()
    {
        var notifier = new RecordingNotifier();
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
            owner = await OwnerSeed.StayOwnerAsync(c);
        var jobs = await SeedJobsAsync(owner, 4);
        var rules = await SeedRulesAsync(owner, 5);
        var plusRow = await BuyPlusAsync(owner.UserId);

        // L3: on Plus the daily pass leaves 4 active jobs and 5 live rules alone and says nothing to this owner.
        await BatchAsync(true, notifier);
        Assert.All(await JobsOfAsync(owner.UserId), j => Assert.Equal(StayJobStatuses.Active, j.Status));
        Assert.All(await RulesOfAsync(owner.ListingId), r => Assert.False(r.PausedByPlan));
        Assert.DoesNotContain(notifier.Stored, n => n.UserId == owner.UserId);

        // L6: Plus ends. The newest job stays Active, three go Inactive (kept); the last added rule stays live, four are paused.
        await SetPlanAsync(plusRow, UserPlanStatuses.Expired);
        await BatchAsync(true, notifier);
        var jobsNow = await JobsOfAsync(owner.UserId);
        Assert.Equal(4, jobsNow.Count);
        Assert.Equal([jobs[3].Id], jobsNow.Where(j => j.Status == StayJobStatuses.Active).Select(j => j.Id).ToList());
        Assert.Equal(3, jobsNow.Count(j => j.Status == StayJobStatuses.Inactive));
        var rulesNow = await RulesOfAsync(owner.ListingId);
        Assert.Equal(5, rulesNow.Count);
        Assert.Equal([rules[4].Id], rulesNow.Where(r => !r.PausedByPlan).Select(r => r.Id).ToList());
        Assert.Equal(4, rulesNow.Count(r => r.PausedByPlan));

        // ONE stored notice and ONE push for this owner.
        Assert.Single(notifier.Stored, n => n.UserId == owner.UserId);
        Assert.Single(notifier.Pushed, p => p.UserId == owner.UserId);
        var versions = rulesNow.ToDictionary(r => r.Id, r => r.Version);

        // The second pass is silent and writes nothing.
        var second = new RecordingNotifier();
        await BatchAsync(true, second);
        Assert.DoesNotContain(second.Stored, n => n.UserId == owner.UserId);
        Assert.DoesNotContain(second.Pushed, p => p.UserId == owner.UserId);
        Assert.Equal(versions, (await RulesOfAsync(owner.ListingId)).ToDictionary(r => r.Id, r => r.Version));

        // L9: Plus again. Every job is Active, every rule is live, the notice is cleared.
        await BuyPlusAsync(owner.UserId);
        var third = new RecordingNotifier();
        await BatchAsync(true, third);
        Assert.All(await JobsOfAsync(owner.UserId), j => Assert.Equal(StayJobStatuses.Active, j.Status));
        Assert.All(await RulesOfAsync(owner.ListingId), r => Assert.False(r.PausedByPlan));
        Assert.Contains(third.Cleared, x => x.Kind == NoticeKinds.PlanLimitsChanged && x.Users.Contains(owner.UserId));
        Assert.DoesNotContain(third.Pushed, p => p.UserId == owner.UserId);

        // L14: Plus ends again, then payment OFF restores everything; ON hides the same rows again.
        var ownerPlus = (await BuyPlusAsync(owner.UserId));
        await SetPlanAsync(ownerPlus, UserPlanStatuses.Cancelled);
        await using (var c = CreateContext())
            await c.UserPlans.Where(u => u.UserId == owner.UserId && u.Status == UserPlanStatuses.Active).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserPlanStatuses.Cancelled));
        await BatchAsync(true, new RecordingNotifier());
        Assert.Equal(3, (await JobsOfAsync(owner.UserId)).Count(j => j.Status == StayJobStatuses.Inactive));
        await BatchAsync(false, new RecordingNotifier());
        Assert.All(await JobsOfAsync(owner.UserId), j => Assert.Equal(StayJobStatuses.Active, j.Status));
        Assert.All(await RulesOfAsync(owner.ListingId), r => Assert.False(r.PausedByPlan));
        await BatchAsync(true, new RecordingNotifier());
        Assert.Equal([jobs[3].Id], (await JobsOfAsync(owner.UserId)).Where(j => j.Status == StayJobStatuses.Active).Select(j => j.Id).ToList());
        Assert.Equal([rules[4].Id], (await RulesOfAsync(owner.ListingId)).Where(r => !r.PausedByPlan).Select(r => r.Id).ToList());
    }

    [IntegrationFact]
    public async Task Jobs_closed_job_never_moves_and_closing_the_active_job_on_free_activates_the_next_one()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
            owner = await OwnerSeed.StayOwnerAsync(c);
        var jobs = await SeedJobsAsync(owner, 4);
        var plusRow = await BuyPlusAsync(owner.UserId);
        await SetPlanAsync(plusRow, UserPlanStatuses.Expired);
        await ApplyAsync(owner.UserId, true);
        Assert.Equal([jobs[3].Id], (await JobsOfAsync(owner.UserId)).Where(j => j.Status == StayJobStatuses.Active).Select(j => j.Id).ToList());

        // The owner closes the active job (what the Close handler writes), then the pass runs under the same lock.
        await using (var c = CreateContext())
            await c.StayJobs.Where(j => j.Id == jobs[3].Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, StayJobStatuses.Closed).SetProperty(j => j.ClosedAt, DateTime.UtcNow));
        await ApplyAsync(owner.UserId, true);
        var after = await JobsOfAsync(owner.UserId);
        Assert.Equal(StayJobStatuses.Closed, after.Single(j => j.Id == jobs[3].Id).Status);
        Assert.Equal([jobs[2].Id], after.Where(j => j.Status == StayJobStatuses.Active).Select(j => j.Id).ToList());
        Assert.Equal(2, after.Count(j => j.Status == StayJobStatuses.Inactive));

        // A new Plus brings the inactive ones back; the closed one stays closed.
        await BuyPlusAsync(owner.UserId);
        await ApplyAsync(owner.UserId, true);
        var plus = await JobsOfAsync(owner.UserId);
        Assert.Equal(StayJobStatuses.Closed, plus.Single(j => j.Id == jobs[3].Id).Status);
        Assert.Equal(3, plus.Count(j => j.Status == StayJobStatuses.Active));
    }

    // The plan effect counts paused special prices like hidden rooms, photos and inactive jobs.
    [IntegrationFact]
    public async Task Special_price_only_loss_is_counted_and_a_grant_that_leaves_rules_paused_does_not_look_clean()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
            owner = await OwnerSeed.StayOwnerAsync(c);
        await SeedRulesAsync(owner, 3);
        var plusRow = await BuyPlusAsync(owner.UserId);
        await SetPlanAsync(plusRow, UserPlanStatuses.Expired);

        var expired = await ApplyAsync(owner.UserId, true);
        Assert.Equal((0, 2), (expired.HiddenBefore, expired.HiddenAfter));
        Assert.True(expired.Changed);

        // A grant whose plan allows only one special price: two rules stay paused, so HiddenAfter is not 0 and the notice stays.
        var partial = Be3.Be3Kit.Plus(ListingKinds.Stay) with { SpecialPrices = 1 };
        var effect = await ApplyWithAsync(owner.UserId, partial);
        Assert.Equal(2, effect.HiddenAfter);
        Assert.False(effect.Changed);

        // A grant with room for all three clears them.
        var full = await ApplyWithAsync(owner.UserId, Be3.Be3Kit.Plus(ListingKinds.Stay));
        Assert.Equal((2, 0), (full.HiddenBefore, full.HiddenAfter));
        Assert.All(await RulesOfAsync(owner.ListingId), r => Assert.False(r.PausedByPlan));
    }

    // Service level: what the owner can still do to a plan-paused special price, and the typed refusal at the limit.
    [IntegrationFact]
    public async Task Special_prices_paused_rows_refuse_edit_pause_allow_delete_and_create_is_refused_at_the_limit_with_the_right_can_upgrade()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
            owner = await OwnerSeed.StayOwnerAsync(c);
        var rules = await SeedRulesAsync(owner, 5);
        var plusRow = await BuyPlusAsync(owner.UserId);
        await SetPlanAsync(plusRow, UserPlanStatuses.Expired);
        await ApplyAsync(owner.UserId, true);
        var today = Be2Kit.Today;
        var paused = rules[0];

        await using (var c = CreateContext())
        {
            var edit = await new Be2Kit(c).Prices.UpdateAsync(owner.UserId, paused.Id, new UpdateSpecialPriceRequest
            {
                DateFrom = paused.DateFrom, DateTo = paused.DateTo, Direction = paused.Direction, Percent = 20, Version = 2,
            });
            Assert.Equal(OwnerErrorTypes.PlanRowInactive, edit.ErrorType);
            var pause = await new Be2Kit(c).Prices.PauseAsync(owner.UserId, paused.Id, new PauseSpecialPriceRequest { Paused = true, Version = 2 });
            Assert.Equal(OwnerErrorTypes.PlanRowInactive, pause.ErrorType);
        }

        var request = new CreateSpecialPriceRequest
        {
            Id = Guid.NewGuid(), DateFrom = today.AddDays(90), DateTo = today.AddDays(92), Direction = SpecialPriceDirections.Higher, Percent = 20,
        };
        await using (var c = CreateContext())
        {
            // L2: Free with all five still counted (paused ones included): refused, the Plus sheet opens (canUpgrade true).
            var refused = await new Be2Kit(c).Prices.CreateAsync(owner.UserId, request);
            Assert.Equal(OwnerErrorTypes.SpecialPriceLimit, refused.ErrorType);
            var data = Assert.IsType<PlanLimitRefusalDto>(refused.Data);
            Assert.Equal((PlanFeatureKeys.SpecialPrices, 1, true), (data.Key, data.Limit, data.CanUpgrade));
        }

        await using (var c = CreateContext())
            Assert.True((await new Be2Kit(c).Prices.DeleteAsync(owner.UserId, paused.Id)).Ok);
        Assert.Equal(4, (await RulesOfAsync(owner.ListingId)).Count);

        // L4: Plus again with four active rules still allows one more; at five it is refused with canUpgrade false.
        await BuyPlusAsync(owner.UserId);
        await using (var c = CreateContext())
            Assert.True((await new Be2Kit(c).Prices.CreateAsync(owner.UserId, request)).Ok);
        await using (var c = CreateContext())
        {
            var full = await new Be2Kit(c).Prices.CreateAsync(owner.UserId, new CreateSpecialPriceRequest
            {
                Id = Guid.NewGuid(), DateFrom = today.AddDays(120), DateTo = today.AddDays(122), Direction = SpecialPriceDirections.Higher, Percent = 20,
            });
            Assert.Equal(OwnerErrorTypes.SpecialPriceLimit, full.ErrorType);
            Assert.False(Assert.IsType<PlanLimitRefusalDto>(full.Data).CanUpgrade);
        }
    }
}
