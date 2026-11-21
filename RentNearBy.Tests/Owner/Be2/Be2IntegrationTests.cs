using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Owner.Be2;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be2;

// Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it (WRITTEN, NOT RUN on the build machine). Calls the services the BE2
// handlers call: every endpoint's happy path, each error type of appendix A, another owner's row, the races of 4.4 and the
// query budgets of 11.3.
public class Be2IntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static DateOnly Today => Be2Kit.Today;
    private OwnerSeed.OwnerRow _owner = null!;

    private async Task<OwnerSeed.OwnerRow> StayAsync(int rooms = 2)
    {
        await using var ctx = CreateContext();
        var owner = await OwnerSeed.StayOwnerAsync(ctx);
        if (rooms > 0) await OwnerSeed.RoomsAsync(ctx, owner.ListingId, rooms);
        return owner;
    }

    private static CreateSpecialPriceRequest Create(DateOnly from, int nights = 3, string direction = SpecialPriceDirections.Higher, int percent = 20, Guid? id = null)
        => new() { Id = id ?? Guid.NewGuid(), DateFrom = from, DateTo = from.AddDays(nights - 1), Direction = direction, Percent = percent };

    private async Task<List<StaySpecialPrice>> RulesAsync(Guid stayId)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<StaySpecialPrice>().AsNoTracking().Where(p => p.StayListingId == stayId).OrderBy(p => p.DateFrom).ToListAsync();
    }

    private async Task<(DateOnly? Day, short Count)> SavesAsync(Guid stayId)
    {
        await using var ctx = CreateContext();
        var s = await ctx.StayListings.AsNoTracking().Where(x => x.Id == stayId).Select(x => new { x.SpecialPriceSavesDay, x.SpecialPriceSavesCount }).SingleAsync();
        return (s.SpecialPriceSavesDay, s.SpecialPriceSavesCount);
    }

    // ---------- P2 ----------

    [IntegrationFact]
    [PlanKeyTest(PlanFeatureKeys.SpecialPrices)]
    public async Task SpecialPrices_SecondOnFree_Refused_and_while_payment_off_the_plus_number_applies()
    {
        var owner = await StayAsync();
        await using (var ctx = CreateContext())
        {
            var first = await new Be2Kit(ctx).Prices.CreateAsync(owner.UserId, Create(Today.AddDays(5)));
            Assert.True(first.Ok);
            var second = await new Be2Kit(ctx).Prices.CreateAsync(owner.UserId, Create(Today.AddDays(20)));
            Assert.Equal(OwnerErrorTypes.SpecialPriceLimit, second.ErrorType);
            Assert.Equal("1 special price on Free.", second.Message);
            Assert.Equal(new PlanLimitRefusalDto(PlanFeatureKeys.SpecialPrices, 1, 1, true), second.Data);
        }
        await using (var ctx = CreateContext())
        {
            var off = await new Be2Kit(ctx, paymentOn: false).Prices.CreateAsync(owner.UserId, Create(Today.AddDays(20)));
            Assert.True(off.Ok);
        }
    }

    [IntegrationFact]
    public async Task Create_writes_the_row_counts_one_save_and_copies_the_festival_name()
    {
        var owner = await StayAsync();
        FestivalDate diwali;
        await using (var ctx = CreateContext()) diwali = await OwnerSeed.FestivalAsync(ctx, "Diwali", Today.AddDays(10), Today.AddDays(16));
        await using (var ctx = CreateContext())
        {
            var kit = new Be2Kit(ctx);
            var request = Create(Today.AddDays(10), 7);
            request.FestivalDateId = diwali.Id;
            var result = await kit.Prices.CreateAsync(owner.UserId, request);
            Assert.True(result.Ok);
            Assert.True(result.Created);
            Assert.Equal("Diwali", result.Value!.Item.Label);
            Assert.Equal(7, result.Value.Item.Nights);
            Assert.Equal(2, result.Value.SavesLeftToday);
            Assert.Equal(new MeterDto(PlanFeatureKeys.SpecialPrices, 1, 1), Assert.Single(result.Value.Meters));
            Assert.Contains(kit.Notifier.Cleared, c => c.Kind == NoticeKinds.FestivalReminder && c.EntityId == diwali.Id && c.Users.Single() == owner.UserId);
        }
        Assert.Equal((Today, (short)1), await SavesAsync(owner.ListingId));
    }

    [IntegrationFact]
    public async Task The_same_create_twice_is_one_row_and_one_save_and_another_owners_id_is_refused()
    {
        var owner = await StayAsync();
        var other = await StayAsync();
        var request = Create(Today.AddDays(5));
        await using (var ctx = CreateContext())
        {
            Assert.True((await new Be2Kit(ctx).Prices.CreateAsync(owner.UserId, request)).Ok);
            var replay = await new Be2Kit(ctx).Prices.CreateAsync(owner.UserId, request);
            Assert.True(replay.Ok);
            Assert.True(replay.Replay);
            Assert.Equal(OwnerErrorTypes.IdTaken, (await new Be2Kit(ctx).Prices.CreateAsync(other.UserId, request)).ErrorType);
        }
        Assert.Single(await RulesAsync(owner.ListingId));
        Assert.Equal((short)1, (await SavesAsync(owner.ListingId)).Count);
    }

    [IntegrationFact]
    public async Task Invalid_overlap_and_the_fourth_save_are_refused()
    {
        var owner = await StayAsync();
        await using var ctx = CreateContext();
        var prices = new Be2Kit(ctx, paymentOn: false).Prices;
        var invalid = await prices.CreateAsync(owner.UserId, Create(Today.AddDays(181)));
        Assert.Equal(OwnerErrorTypes.SpecialPriceInvalid, invalid.ErrorType);
        Assert.Equal(OwnerErrorTypes.SpecialPriceStartTooLate, invalid.Message);
        Assert.Equal(OwnerErrorTypes.SpecialPricePercentLower, (await prices.CreateAsync(owner.UserId, Create(Today, percent: 60, direction: SpecialPriceDirections.Lower))).Message);

        var diwali = await prices.CreateAsync(owner.UserId, Create(Today.AddDays(10), 7));
        var overlap = await prices.CreateAsync(owner.UserId, Create(Today.AddDays(12)));
        Assert.Equal(OwnerErrorTypes.SpecialPriceOverlap, overlap.ErrorType);
        Assert.Equal(diwali.Value!.Item.Id, ((SpecialPriceOverlapDto)overlap.Data!).Id);

        Assert.True((await prices.CreateAsync(owner.UserId, Create(Today.AddDays(30)))).Ok);
        Assert.True((await prices.CreateAsync(owner.UserId, Create(Today.AddDays(40)))).Ok);
        var fourth = await prices.CreateAsync(owner.UserId, Create(Today.AddDays(50)));
        Assert.Equal(OwnerErrorTypes.SpecialPriceSavesLimit, fourth.ErrorType);
        Assert.Equal(new DailyLimitRefusalDto(3, 3), fourth.Data);
    }

    [IntegrationFact]
    public async Task A_paused_rule_still_blocks_an_overlap_and_counts_for_the_limit()
    {
        var owner = await StayAsync();
        await using (var ctx = CreateContext())
        {
            var rows = await OwnerSeed.SpecialPricesAsync(ctx, owner.ListingId, 1, Today.AddDays(5));
            await ctx.Set<StaySpecialPrice>().Where(p => p.Id == rows[0].Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsPaused, true));
        }
        await using (var ctx = CreateContext())
        {
            var prices = new Be2Kit(ctx, paymentOn: false).Prices;
            Assert.Equal(OwnerErrorTypes.SpecialPriceOverlap, (await prices.CreateAsync(owner.UserId, Create(Today.AddDays(6)))).ErrorType);
            Assert.Equal(OwnerErrorTypes.SpecialPriceLimit, (await new Be2Kit(ctx).Prices.CreateAsync(owner.UserId, Create(Today.AddDays(30)))).ErrorType);
        }
    }

    [IntegrationFact]
    public async Task No_listing_is_404_listing_not_found()
    {
        await using var ctx = CreateContext();
        var seeker = await OwnerSeed.UserAsync(ctx, UserRoles.SeekerId);
        var kit = new Be2Kit(ctx);
        Assert.Equal(OwnerErrorTypes.ListingNotFound, (await kit.Prices.ListAsync(seeker)).ErrorType);
        Assert.Equal(OwnerErrorTypes.ListingNotFound, (await kit.Prices.CreateAsync(seeker, Create(Today))).ErrorType);
    }

    // ---------- P3 / P4 / P5 / P6 ----------

    [IntegrationFact]
    public async Task Edit_moves_the_dates_bumps_the_version_and_a_stale_edit_gets_the_current_row()
    {
        var owner = await StayAsync();
        await using var ctx = CreateContext();
        var prices = new Be2Kit(ctx).Prices;
        var created = (await prices.CreateAsync(owner.UserId, Create(Today.AddDays(5)))).Value!.Item;
        var edit = new UpdateSpecialPriceRequest { DateFrom = Today.AddDays(6), DateTo = Today.AddDays(9), Direction = SpecialPriceDirections.Lower, Percent = 15, Version = created.Version };
        var updated = await prices.UpdateAsync(owner.UserId, created.Id, edit);
        Assert.True(updated.Ok);
        Assert.Equal(2, updated.Value!.Item.Version);
        Assert.Equal(4, updated.Value.Item.Nights);

        var replay = await prices.UpdateAsync(owner.UserId, created.Id, edit);
        Assert.True(replay.Replay);
        edit.Percent = 25;
        var stale = await prices.UpdateAsync(owner.UserId, created.Id, edit);
        Assert.Equal(OwnerErrorTypes.StaleEdit, stale.ErrorType);
        Assert.Equal(2, ((StaleEditDto<SpecialPriceItemDto>)stale.Data!).Current.Version);
    }

    [IntegrationFact]
    public async Task Another_owners_rule_is_404_for_edit_pause_live_and_delete_leaves_it()
    {
        var owner = await StayAsync();
        var other = await StayAsync();
        Guid id;
        await using (var ctx = CreateContext()) id = (await OwnerSeed.SpecialPricesAsync(ctx, other.ListingId, 1, Today.AddDays(5)))[0].Id;
        await using (var ctx = CreateContext())
        {
            var prices = new Be2Kit(ctx).Prices;
            var edit = new UpdateSpecialPriceRequest { DateFrom = Today.AddDays(5), DateTo = Today.AddDays(6), Direction = SpecialPriceDirections.Higher, Percent = 20, Version = 1 };
            Assert.Equal(Be2Result<SpecialPriceWriteDto>.NotFound, (await prices.UpdateAsync(owner.UserId, id, edit)).ErrorType);
            Assert.Equal(Be2Result<SpecialPriceWriteDto>.NotFound, (await prices.PauseAsync(owner.UserId, id, new PauseSpecialPriceRequest { Paused = true, Version = 1 })).ErrorType);
            Assert.Equal(Be2Result<SpecialPriceWriteDto>.NotFound, (await prices.MakeLiveAsync(owner.UserId, id, new LiveSpecialPriceRequest { Version = 1 })).ErrorType);
            Assert.True((await prices.DeleteAsync(owner.UserId, id)).Ok);
        }
        Assert.Single(await RulesAsync(other.ListingId));
    }

    [IntegrationFact]
    public async Task Pause_and_resume_count_as_saves_a_repeat_does_not_and_delete_is_idempotent()
    {
        var owner = await StayAsync();
        await using var ctx = CreateContext();
        var prices = new Be2Kit(ctx).Prices;
        var item = (await prices.CreateAsync(owner.UserId, Create(Today.AddDays(5)))).Value!.Item;
        var paused = await prices.PauseAsync(owner.UserId, item.Id, new PauseSpecialPriceRequest { Paused = true, Version = item.Version });
        Assert.True(paused.Value!.Item.IsPaused);
        Assert.False(paused.Value.Item.Live);
        Assert.True((await prices.PauseAsync(owner.UserId, item.Id, new PauseSpecialPriceRequest { Paused = true, Version = item.Version })).Replay);
        Assert.Equal((short)2, (await SavesAsync(owner.ListingId)).Count);

        Assert.True((await prices.DeleteAsync(owner.UserId, item.Id)).Changed);
        var again = await prices.DeleteAsync(owner.UserId, item.Id);
        Assert.True(again.Ok);
        Assert.False(again.Changed);
        Assert.Equal((short)3, (await SavesAsync(owner.ListingId)).Count);
    }

    [IntegrationFact]
    public async Task Make_live_swaps_the_plan_paused_rule_with_the_live_one_on_free()
    {
        var owner = await StayAsync();
        List<StaySpecialPrice> rows;
        await using (var ctx = CreateContext())
        {
            rows = await OwnerSeed.SpecialPricesAsync(ctx, owner.ListingId, 2, Today.AddDays(5));
            await ctx.Set<StaySpecialPrice>().Where(p => p.Id == rows[1].Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.PausedByPlan, true));
        }
        await using (var ctx = CreateContext())
        {
            var result = await new Be2Kit(ctx).Prices.MakeLiveAsync(owner.UserId, rows[1].Id, new LiveSpecialPriceRequest { Version = 1 });
            Assert.True(result.Ok);
            Assert.True(result.Value!.Item.Live);
        }
        var after = await RulesAsync(owner.ListingId);
        Assert.True(after.Single(p => p.Id == rows[0].Id).PausedByPlan);
        Assert.False(after.Single(p => p.Id == rows[1].Id).PausedByPlan);
        await using (var ctx = CreateContext())
            Assert.True((await new Be2Kit(ctx).Prices.MakeLiveAsync(owner.UserId, rows[1].Id, new LiveSpecialPriceRequest { Version = 2 })).Replay);
    }

    // ---------- races (4.4) ----------

    [IntegrationFact]
    public async Task Two_phones_save_a_second_rule_on_free_exactly_one_succeeds()
    {
        var owner = await StayAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
        {
            await using var ctx = CreateContext();
            return await new Be2Kit(ctx).Prices.CreateAsync(owner.UserId, Create(Today.AddDays(5 + i * 20)));
        }));
        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.Equal(OwnerErrorTypes.SpecialPriceLimit, results.Single(r => !r.Ok).ErrorType);
    }

    [IntegrationFact]
    public async Task Two_overlapping_rules_saved_together_the_second_sees_the_first()
    {
        var owner = await StayAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
        {
            await using var ctx = CreateContext();
            return await new Be2Kit(ctx, paymentOn: false).Prices.CreateAsync(owner.UserId, Create(Today.AddDays(5 + i)));
        }));
        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.Equal(OwnerErrorTypes.SpecialPriceOverlap, results.Single(r => !r.Ok).ErrorType);
    }

    [IntegrationFact]
    public async Task Two_phones_edit_one_rule_the_second_gets_stale_edit()
    {
        var owner = await StayAsync();
        Guid id;
        await using (var ctx = CreateContext()) id = (await OwnerSeed.SpecialPricesAsync(ctx, owner.ListingId, 1, Today.AddDays(5)))[0].Id;
        var results = await Task.WhenAll(new[] { 15, 25 }.Select(async percent =>
        {
            await using var ctx = CreateContext();
            return await new Be2Kit(ctx).Prices.UpdateAsync(owner.UserId, id, new UpdateSpecialPriceRequest
            {
                DateFrom = Today.AddDays(5), DateTo = Today.AddDays(7), Direction = SpecialPriceDirections.Lower, Percent = percent, Version = 1,
            });
        }));
        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.Equal(OwnerErrorTypes.StaleEdit, results.Single(r => !r.Ok).ErrorType);
    }

    // ---------- query counts (11.3) ----------

    private async Task SeedRulesAsync(ApplicationDbContext ctx, int rows)
    {
        _owner = await OwnerSeed.StayOwnerAsync(ctx);
        await OwnerSeed.RoomsAsync(ctx, _owner.ListingId, Math.Min(rows, 7));
        await OwnerSeed.SpecialPricesAsync(ctx, _owner.ListingId, rows, Today, nights: 1);
        await OwnerSeed.BlocksAsync(ctx, _owner.ListingId, rows, Today, nights: 1);
    }

    [IntegrationFact]
    public Task P1_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedRulesAsync,
        async ctx => Assert.True((await new Be2Kit(ctx).Prices.ListAsync(_owner.UserId)).Ok), budget: 3);

    [IntegrationFact]
    public Task P2_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedRulesAsync,
        async ctx => await new Be2Kit(ctx, paymentOn: false).Prices.CreateAsync(_owner.UserId, Create(Today.AddDays(170), 1)), budget: 6);

    [IntegrationFact]
    public Task F1_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db,
        async (ctx, rows) =>
        {
            for (var i = 0; i < rows; i++) await OwnerSeed.FestivalAsync(ctx, $"Fest {i}", Today.AddDays(i), Today.AddDays(i + 1));
        },
        async ctx => Assert.True((await new Be2Kit(ctx).FestivalAdmin.ListAsync(null, null, null)).Ok), budget: 1);

    [IntegrationFact]
    public Task T2_and_T4_price_facts_are_one_statement_for_one_or_many_stays() => QueryAssert.SameCountAsync(Db, SeedRulesAsync,
        async ctx => Assert.Single(await StayPriceFacts.LoadAsync(ctx, [_owner.ListingId], Today)), budget: 1);

    [IntegrationFact]
    public Task T1_nearby_carries_rules_and_blocks_with_no_extra_statement() => QueryAssert.SameCountAsync(Db, SeedRulesAsync,
        async ctx =>
        {
            var rows = (await new StayListingRepository(ctx).GetNearbyAsync(29.3919, 79.4542, 2, null, null)).ToList();
            var mine = rows.Single(r => r.Id == _owner.ListingId);
            Assert.NotNull(mine.Rules);
            Assert.NotNull(mine.Blocks);
        }, budget: 1);

    // ---------- T4 / festivals ----------

    [IntegrationFact]
    public async Task Listed_total_equals_the_price_function_and_blocked_nights_are_seen()
    {
        var owner = await StayAsync(rooms: 1);
        await using (var ctx = CreateContext())
        {
            await OwnerSeed.SpecialPricesAsync(ctx, owner.ListingId, 1, Today.AddDays(11), nights: 2, direction: SpecialPriceDirections.Higher, percent: 20);
            await OwnerSeed.BlocksAsync(ctx, owner.ListingId, 1, Today.AddDays(20), nights: 2);
        }
        await using (var ctx = CreateContext())
        {
            var facts = (await StayPriceFacts.LoadAsync(ctx, [owner.ListingId], Today))[owner.ListingId];
            var quote = StayListedQuote.Compute(facts.Rooms, null, Today.AddDays(10), 3, facts.Rules)!;
            Assert.Equal(3000 + 3600 + 3600, quote.Total);
            Assert.True(StaySpecialPriceMath.TouchesBlock(Today.AddDays(19), 2, facts.Blocks));
            Assert.False(StaySpecialPriceMath.TouchesBlock(Today.AddDays(18), 2, facts.Blocks));
        }
    }

    [IntegrationFact]
    public async Task Admin_festival_create_is_idempotent_update_and_delete_drop_the_cache()
    {
        await using var ctx = CreateContext();
        var kit = new Be2Kit(ctx);
        var request = new AdminFestivalRequest { Id = Guid.NewGuid(), Name = "Diwali", DateFrom = Today.AddDays(30), DateTo = Today.AddDays(36) };
        Assert.True((await kit.FestivalAdmin.CreateAsync(request)).Created);
        Assert.True((await kit.FestivalAdmin.CreateAsync(request)).Replay);
        Assert.Contains((await kit.FestivalAdmin.ListAsync(null, null, null)).Value!.Items, f => f.Id == request.Id);

        request.Name = "Deepavali";
        Assert.Equal("Deepavali", (await kit.FestivalAdmin.UpdateAsync(request.Id, request)).Value!.Name);
        Assert.Contains((await kit.FestivalAdmin.ListAsync(null, null, null)).Value!.Items, f => f.Name == "Deepavali");

        var page = await kit.FestivalAdmin.PageAsync(null);
        Assert.Contains(page.Items, f => f.Id == request.Id);

        await kit.FestivalAdmin.DeleteAsync(request.Id);
        Assert.DoesNotContain((await kit.FestivalAdmin.ListAsync(null, null, null)).Value!.Items, f => f.Id == request.Id);
        Assert.Equal(Be2Result<AdminFestivalDto>.NotFound, (await kit.FestivalAdmin.UpdateAsync(request.Id, request)).ErrorType);
        Assert.Equal(OwnerErrorTypes.BadRequest, (await kit.FestivalAdmin.CreateAsync(new AdminFestivalRequest
        {
            Id = Guid.NewGuid(), Name = "Holi", DateFrom = Today, DateTo = Today, StateName = "No Such State",
        })).ErrorType);
    }

    [IntegrationFact]
    public async Task Festival_list_matches_the_state_or_all_india()
    {
        await using var ctx = CreateContext();
        var all = await OwnerSeed.FestivalAsync(ctx, "Diwali", Today.AddDays(3), Today.AddDays(5));
        var state = await OwnerSeed.FestivalAsync(ctx, "Harela", Today.AddDays(8), Today.AddDays(8), "Uttarakhand");
        var kit = new Be2Kit(ctx);
        var mine = (await kit.FestivalAdmin.ListAsync("Uttarakhand", null, null)).Value!.Items;
        Assert.Contains(mine, f => f.Id == all.Id);
        Assert.Contains(mine, f => f.Id == state.Id);
        var other = (await kit.FestivalAdmin.ListAsync("Kerala", null, null)).Value!.Items;
        Assert.DoesNotContain(other, f => f.Id == state.Id);
    }
}
