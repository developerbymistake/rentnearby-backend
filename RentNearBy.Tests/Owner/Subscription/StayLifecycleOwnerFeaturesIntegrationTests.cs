using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Owner.Be2;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Subscription;

// Subscription matrix (Stay owner) on a real database: calendar entries, blocked ranges, upcoming days, months ahead, month money
// summary and the morning reminder switch, through Plus, expire, re-subscribe and payment OFF, plus the two limit races (L15).
// The owner context is read by the real OwnerContextReader from the owner's real plan rows. Needs RENTNEARBY_TEST_DB (PostGIS);
// WRITTEN, NOT RUN on the build machine.
public class StayLifecycleOwnerFeaturesIntegrationTests(TestDatabase db) : StayLifecycleDbBase(db)
{
    private static DateOnly Today => Be2Kit.Today;

    private static async Task<OwnerContext> CtxAsync(ApplicationDbContext c, Guid userId, bool paymentOn)
    {
        var kit = new Be2Kit(c, paymentOn);
        var reader = new OwnerCalendarReader(c, kit.Contexts, kit.Festivals);
        return (await reader.ContextAsync(userId, ListingKinds.Stay))!;
    }

    private static OwnerCalendarReader Reader(ApplicationDbContext c, bool paymentOn)
    {
        var kit = new Be2Kit(c, paymentOn);
        return new OwnerCalendarReader(c, kit.Contexts, kit.Festivals);
    }

    private static OwnerCalendarWriter Writer(ApplicationDbContext c) => new(c, NullLogger<OwnerCalendarWriter>.Instance);

    private static BlockedRangeService Blocks(ApplicationDbContext c) => new(c, NullLogger<BlockedRangeService>.Instance);

    private static ManualEntryInput Entry(int fromDays, string label = "Phone booking")
        => new(label, Today.AddDays(fromDays), Today.AddDays(fromDays + 1), null, null, null, null, default);

    [IntegrationFact]
    public async Task Calendar_entries_blocks_window_summary_and_reminder_walk_plus_expire_resubscribe_off()
    {
        OwnerSeed.OwnerRow owner;
        List<CalendarEntry> entries;
        List<StayBlockedRange> blocks;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            entries = await OwnerSeed.ManualEntriesAsync(c, owner, 10, Today.AddDays(1), stepDays: 2);
            blocks = await OwnerSeed.BlocksAsync(c, owner.ListingId, 6, Today.AddDays(5));
        }
        var plusRow = await BuyPlusAsync(owner.UserId);

        // L3 / L4: Plus. All ten entries and six blocks count and are readable; summary and reminder are open; the window is 183 days.
        await using (var c = CreateContext())
        {
            var ctx = await CtxAsync(c, owner.UserId, true);
            Assert.Equal((50, 10, 183, 6), (ctx.Limits.CalendarEntries, ctx.Limits.BlockedRanges, ctx.Limits.UpcomingDays, ctx.Limits.MonthsAhead));
            Assert.Equal((10L, 6L), (OwnerCalendarReader.ActiveManual(ctx), OwnerCalendarReader.ActiveBlocks(ctx)));
            Assert.True((await Reader(c, true).SummaryAsync(ctx, null)).Ok);
            Assert.True((await Writer(c).SetReminderAsync(ctx, true)).Ok);
            var upcoming = await Reader(c, true).UpcomingAsync(ctx, null);
            Assert.DoesNotContain(upcoming.Items, i => i.InactiveByPlan);
            Assert.Equal(0, upcoming.HiddenCount);
        }

        // L6: Plus ends. Nothing is deleted; blocks keep blocking; only NEW adds are refused with the typed error and canUpgrade true.
        await SetPlanAsync(plusRow, UserPlanStatuses.Expired);
        await ApplyAsync(owner.UserId, true);
        await using (var c = CreateContext())
        {
            var ctx = await CtxAsync(c, owner.UserId, true);
            Assert.Equal((4, 1, 15, 0), (ctx.Limits.CalendarEntries, ctx.Limits.BlockedRanges, ctx.Limits.UpcomingDays, ctx.Limits.MonthsAhead));
            Assert.True(ctx.Limits.CanUpgrade);
            Assert.Equal((10L, 6L), (OwnerCalendarReader.ActiveManual(ctx), OwnerCalendarReader.ActiveBlocks(ctx)));
            Assert.Equal(6, (await Blocks(c).ListAsync(ctx)).Count);

            var block = await Blocks(c).CreateAsync(ctx, Guid.NewGuid(), Today.AddDays(120), Today.AddDays(121), null, false);
            Assert.Equal(OwnerErrorTypes.BlockedRangeLimit, block.Error!.Type);
            var blockData = Assert.IsType<PlanLimitRefusalDto>(block.Error.Data);
            Assert.Equal((PlanFeatureKeys.BlockedRanges, 1, 6, true), (blockData.Key, blockData.Limit, blockData.Used, blockData.CanUpgrade));

            var entry = await Writer(c).CreateAsync(ctx, Guid.NewGuid(), Entry(3));
            Assert.Equal(OwnerErrorTypes.CalendarEntryLimit, entry.Error!.Type);
            var entryData = Assert.IsType<PlanLimitRefusalDto>(entry.Error.Data);
            Assert.Equal((PlanFeatureKeys.CalendarEntries, 4, 10, true), (entryData.Key, entryData.Limit, entryData.Used, entryData.CanUpgrade));

            // Summary is locked again, the reminder cannot be turned on, but it can be turned off; the stored flag survived expiry.
            Assert.Equal(OwnerErrorTypes.PlanFeatureLocked, (await Reader(c, true).SummaryAsync(ctx, null)).Error!.Type);
            Assert.Equal(OwnerErrorTypes.PlanFeatureLocked, (await Writer(c).SetReminderAsync(ctx, true)).Error!.Type);
            Assert.True(await c.StayListings.AsNoTracking().Where(s => s.Id == owner.ListingId).Select(s => s.RemindersOn).SingleAsync());

            // Only the last four added entries are active; the others are listed inactive (flagged) so they can be deleted.
            var upcoming = await Reader(c, true).UpcomingAsync(ctx, null);
            Assert.True(upcoming.HiddenCount >= 6);
            var inactiveIds = upcoming.Items.Where(i => i.InactiveByPlan).Select(i => i.Id).ToList();
            Assert.All(inactiveIds, id => Assert.Contains(id, entries.Take(6).Select(e => e.Id)));
        }
        await using (var c = CreateContext())
            Assert.Equal(10, await c.Set<CalendarEntry>().CountAsync(e => e.ListingId == owner.ListingId));
        await using (var c = CreateContext())
            Assert.Equal(6, await c.Set<StayBlockedRange>().CountAsync(b => b.StayListingId == owner.ListingId));

        // Delete stays open: an inactive (oldest) entry and a block.
        await using (var c = CreateContext())
        {
            var ctx = await CtxAsync(c, owner.UserId, true);
            Assert.Null(await Writer(c).DeleteAsync(ctx, entries[0].Id));
            Assert.Equal(BlockedRangeService.DeleteOutcome.Deleted, await Blocks(c).DeleteAsync(ctx, blocks[0].Id));
        }

        // L9: re-subscribe. Limits are Plus again; everything is active and flagged nothing; adds work up to the Plus limit.
        await BuyPlusAsync(owner.UserId);
        await ApplyAsync(owner.UserId, true);
        await using (var c = CreateContext())
        {
            var ctx = await CtxAsync(c, owner.UserId, true);
            Assert.Equal((50, 10), (ctx.Limits.CalendarEntries, ctx.Limits.BlockedRanges));
            Assert.False(ctx.Limits.CanUpgrade);
            var upcoming = await Reader(c, true).UpcomingAsync(ctx, null);
            Assert.DoesNotContain(upcoming.Items, i => i.InactiveByPlan);
            Assert.True((await Blocks(c).CreateAsync(ctx, Guid.NewGuid(), Today.AddDays(120), Today.AddDays(121), null, false)).Ok);
            Assert.True((await Writer(c).CreateAsync(ctx, Guid.NewGuid(), Entry(40))).Ok);
            Assert.True((await Reader(c, true).SummaryAsync(ctx, null)).Ok);
            Assert.True((await Writer(c).SetReminderAsync(ctx, true)).Ok);
        }

        // L14: payment OFF with no plan at all behaves like Plus (nothing refused for the plan, never an upgrade offer).
        await using (var c = CreateContext())
            await c.UserPlans.Where(u => u.UserId == owner.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserPlanStatuses.Cancelled));
        await using (var c = CreateContext())
        {
            var ctx = await CtxAsync(c, owner.UserId, false);
            Assert.Equal((50, 10, 183, 6), (ctx.Limits.CalendarEntries, ctx.Limits.BlockedRanges, ctx.Limits.UpcomingDays, ctx.Limits.MonthsAhead));
            Assert.False(ctx.Limits.CanUpgrade);
            Assert.True((await Reader(c, false).SummaryAsync(ctx, null)).Ok);
            Assert.True((await Writer(c).CreateAsync(ctx, Guid.NewGuid(), Entry(50))).Ok);
        }
    }

    // L15: two phones save the last free slot at the same time. The listing lock lets exactly one win.
    [IntegrationFact]
    public async Task Two_blocks_at_the_free_limit_at_the_same_time_exactly_one_wins()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
            owner = await OwnerSeed.StayOwnerAsync(c);

        async Task<Be1Result<BlockedRangeWrite>> TryAsync(int from)
        {
            await using var c = CreateContext();
            var ctx = await CtxAsync(c, owner.UserId, true);
            return await Blocks(c).CreateAsync(ctx, Guid.NewGuid(), Today.AddDays(from), Today.AddDays(from + 1), null, false);
        }

        var results = await Task.WhenAll(TryAsync(10), TryAsync(30));
        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.Equal(OwnerErrorTypes.BlockedRangeLimit, results.Single(r => !r.Ok).Error!.Type);
    }

    [IntegrationFact]
    public async Task Two_calendar_entries_for_the_last_free_slot_at_the_same_time_exactly_one_wins()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            await OwnerSeed.ManualEntriesAsync(c, owner, 3, Today.AddDays(1), stepDays: 2);
        }

        async Task<Be1Result<CalendarEntryWrite>> TryAsync(int from)
        {
            await using var c = CreateContext();
            var ctx = await CtxAsync(c, owner.UserId, true);
            return await Writer(c).CreateAsync(ctx, Guid.NewGuid(), Entry(from));
        }

        var results = await Task.WhenAll(TryAsync(8), TryAsync(10));
        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.Equal(OwnerErrorTypes.CalendarEntryLimit, results.Single(r => !r.Ok).Error!.Type);
    }
}
