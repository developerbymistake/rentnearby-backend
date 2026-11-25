using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using RentNearBy.Tests.Owner.Be3;

namespace RentNearBy.Tests.Owner.Subscription;

// Subscription matrix (Stay owner), the plan-limited rows: rooms, photos, jobs, special prices. Every feature is walked through
// Free below the limit, Free at the limit (typed refusal), Plus, Plus full, expire (kept rows, hidden or inactive rows, nothing
// deleted), re-subscribe (everything back in the order it was added), expire again, payment OFF and the second identical pass.
// The pure choice functions are the same ones the single-owner effect and the daily batch use (the SQL twin is pinned by
// PlanRowsChoiceTests); the database walk is StayLifecycleIntegrationTests. WRITTEN, NOT RUN on the build machine.
public class StayLifecycleRowsTests
{
    private static readonly PlanLimits Free = Be3Kit.Free(ListingKinds.Stay);
    private static readonly PlanLimits Plus = Be3Kit.Plus(ListingKinds.Stay);
    private static readonly PlanLimits Off = Be3Kit.Off(ListingKinds.Stay);

    // ---------------------------------------------------------------- limits of the three plan states

    [Fact]
    public void Free_plus_and_off_numbers_of_every_stay_row_key()
    {
        Assert.Equal((2, 2, 1, 1), (Free.RoomTypes, Free.Photos, Free.JobsPosts, Free.SpecialPrices));
        Assert.Equal((6, 8, 4, 5), (Plus.RoomTypes, Plus.Photos, Plus.JobsPosts, Plus.SpecialPrices));
        Assert.Equal((6, 8, 4, 5), (Off.RoomTypes, Off.Photos, Off.JobsPosts, Off.SpecialPrices));
        Assert.True(Free.CanUpgrade);
        Assert.False(Plus.CanUpgrade);
        Assert.False(Off.CanUpgrade);
        Assert.True(Free.PaymentEnabled && Plus.PaymentEnabled);
        Assert.False(Off.PaymentEnabled);
    }

    // ---------------------------------------------------------------- L1 to L4: the typed refusal at the limit

    [Theory]
    [InlineData(PlanFeatureKeys.RoomTypes, OwnerErrorTypes.PlanRoomTypeLimit, "2 room types on Free.", "You can add up to 6 room types.")]
    [InlineData(PlanFeatureKeys.Photos, OwnerErrorTypes.PlanPhotoLimit, "Free plan allows 2 photos.", "You can add up to 8 photos.")]
    [InlineData(PlanFeatureKeys.JobsPosts, OwnerErrorTypes.StayJobLimit, "Your plan allows 1 active job. Close one or upgrade.", "You can have up to 4 active jobs. Close one to post another.")]
    [InlineData(PlanFeatureKeys.SpecialPrices, OwnerErrorTypes.SpecialPriceLimit, "1 special price on Free.", "You can add up to 5 special prices.")]
    [InlineData(PlanFeatureKeys.BlockedRanges, OwnerErrorTypes.BlockedRangeLimit, "Free allows 1 blocked range.", "You can add up to 10 blocked ranges.")]
    [InlineData(PlanFeatureKeys.CalendarEntries, OwnerErrorTypes.CalendarEntryLimit, "You have 4 active entries.", "You can add up to 50 entries.")]
    public void Refusal_at_the_limit_is_a_409_with_the_free_line_and_the_plus_full_line(string key, string errorType, string freeLine, string plusLine)
    {
        Assert.Equal(409, OwnerErrorTypes.StatusOf(errorType));
        Assert.Equal(freeLine, OwnerErrorTypes.LimitMessage(errorType, Free.Get(key), Free.CanUpgrade));
        Assert.Equal(plusLine, OwnerErrorTypes.LimitMessage(errorType, Plus.Get(key), Plus.CanUpgrade));
        Assert.Equal(plusLine, OwnerErrorTypes.LimitMessage(errorType, Off.Get(key), Off.CanUpgrade));

        // The one data shape the app reads: Free at the limit opens the Plus sheet (canUpgrade), Plus full does not.
        var free = new PlanLimitRefusalDto(key, Free.Get(key), Free.Get(key), Free.CanUpgrade);
        var full = new PlanLimitRefusalDto(key, Plus.Get(key), Plus.Get(key), Plus.CanUpgrade);
        Assert.True(free.CanUpgrade);
        Assert.False(full.CanUpgrade);
        Assert.Equal(key, free.Key);
    }

    [Fact]
    public void Rooms_save_allowed_below_the_limit_refused_at_the_limit_and_for_plus_full()
    {
        Assert.True(PlanRowChoice.SaveAllowed(requestCount: 2, Free.RoomTypes, visibleNow: 1));    // L1
        Assert.False(PlanRowChoice.SaveAllowed(requestCount: 3, Free.RoomTypes, visibleNow: 2));   // L2
        Assert.True(PlanRowChoice.SaveAllowed(requestCount: 6, Plus.RoomTypes, visibleNow: 2));    // L3 limits rise at once
        Assert.False(PlanRowChoice.SaveAllowed(requestCount: 7, Plus.RoomTypes, visibleNow: 6));   // L4
        Assert.False(PlanRowChoice.SaveAllowed(requestCount: 7, Off.RoomTypes, visibleNow: 6));    // L14 still capped at the Plus number
        Assert.True(PlanRowChoice.SaveAllowed(requestCount: 6, Off.RoomTypes, visibleNow: 6));
    }

    [Theory]
    [InlineData(0, 0, 2, true)]    // L1 Free, empty
    [InlineData(1, 1, 2, true)]    // L1 Free, below
    [InlineData(2, 2, 2, false)]   // L2 Free at the limit
    [InlineData(2, 5, 2, false)]   // after expiry: 2 visible, 3 hidden, Free still refuses
    [InlineData(7, 7, 8, true)]    // L3 Plus below the limit
    [InlineData(8, 8, 8, false)]   // L4 Plus full
    [InlineData(8, 12, 8, false)]  // the hard ceiling of twelve rows, hidden ones included
    [InlineData(2, 11, 8, true)]   // Plus again with many hidden rows below the ceiling
    public void Photo_upload_gate_per_state(int visible, int all, int limit, bool allowed)
        => Assert.Equal(allowed, PlanRowChoice.PhotoUploadAllowed(visible, all, limit));

    [Fact]
    public void Photo_upload_gate_uses_the_plan_number_of_each_state()
    {
        Assert.False(PlanRowChoice.PhotoUploadAllowed(2, 2, Free.Photos));
        Assert.True(PlanRowChoice.PhotoUploadAllowed(2, 2, Plus.Photos));
        Assert.False(PlanRowChoice.PhotoUploadAllowed(8, 8, Off.Photos));
        Assert.True(PlanRowChoice.PhotoUploadAllowed(7, 7, Off.Photos));
    }

    [Fact]
    public void Jobs_and_special_prices_refuse_only_on_the_active_count()
    {
        // Jobs: the server compares the ACTIVE jobs with the cap (inactive ones never block a new post on Plus).
        var free = StayJobRules.LimitData(Free.JobsPosts, active: 1, Free.CanUpgrade);
        Assert.Equal((PlanFeatureKeys.JobsPosts, 1, 1, true, 1), (free.Key, free.Limit, free.Used, free.CanUpgrade, free.Active));
        var full = StayJobRules.LimitData(Plus.JobsPosts, active: 4, Plus.CanUpgrade);
        Assert.Equal((4, 4, false), (full.Limit, full.Used, full.CanUpgrade));

        // Special prices: every active rule counts, paused and plan-paused included.
        Assert.False(StaySpecialPriceRules.LimitReached(0, Free.SpecialPrices));
        Assert.True(StaySpecialPriceRules.LimitReached(1, Free.SpecialPrices));
        Assert.True(StaySpecialPriceRules.LimitReached(5, Free.SpecialPrices));
        Assert.False(StaySpecialPriceRules.LimitReached(4, Plus.SpecialPrices));
        Assert.True(StaySpecialPriceRules.LimitReached(5, Plus.SpecialPrices));
    }

    // ---------------------------------------------------------------- rooms: L6, L9, L10, L14, idempotent

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public void Rooms_expire_keeps_the_last_added_and_resubscribe_restores_the_order_every_cycle(int count)
    {
        var added = StayLifecycleKit.Rooms(count);
        var ids = added.Select(r => r.Id).ToList();
        var keep = Math.Min(Free.RoomTypes, count);

        // L3 / L4: Plus holds everything, a pass changes nothing.
        Assert.Empty(PlanRowChoice.Changed(added, PlanRowChoice.ChooseIndexed(added, Plus.RoomTypes, Plus.PaymentEnabled)));

        // L6: the last added stay active, older ones go to the hidden band, nothing is deleted.
        var expired = StayLifecycleKit.SettleIndexed(added, Free.RoomTypes, Free.PaymentEnabled);
        Assert.Equal(count, expired.Count);
        Assert.Equal(ids.Skip(count - keep).ToList(), StayLifecycleKit.VisibleIds(expired));
        Assert.Equal(Enumerable.Range(1, keep).ToList(), expired.Where(r => !r.Hidden).Select(r => r.Index).OrderBy(i => i).ToList());
        Assert.All(expired.Where(r => r.Hidden), r => Assert.True(r.Index > PlanRowBands.HiddenBase));

        // The second identical pass writes nothing (one push, no churn).
        Assert.Empty(PlanRowChoice.Changed(expired, PlanRowChoice.ChooseIndexed(expired, Free.RoomTypes, true)));

        // L9: Plus again brings everything back in the order it was added.
        var back = StayLifecycleKit.SettleIndexed(expired, Plus.RoomTypes, Plus.PaymentEnabled);
        Assert.DoesNotContain(back, r => r.Hidden);
        Assert.Equal(ids, StayLifecycleKit.InOrder(back));
        Assert.Equal(Enumerable.Range(1, count).ToList(), back.Select(r => r.Index).OrderBy(i => i).ToList());

        // Second cycle gives the same result as the first.
        var again = StayLifecycleKit.SettleIndexed(back, Free.RoomTypes, true);
        Assert.Equal(StayLifecycleKit.ById(expired), StayLifecycleKit.ById(again));

        // L14: payment OFF clears every hidden flag and keeps the order; on again, the same rows are inactive again.
        var off = StayLifecycleKit.SettleIndexed(expired, Off.RoomTypes, Off.PaymentEnabled);
        Assert.DoesNotContain(off, r => r.Hidden);
        Assert.Equal(ids, StayLifecycleKit.InOrder(off));
        Assert.Equal(StayLifecycleKit.ById(expired), StayLifecycleKit.ById(StayLifecycleKit.SettleIndexed(off, Free.RoomTypes, true)));
    }

    [Fact]
    public void Rooms_delete_while_inactive_brings_back_the_newest_hidden_room()
    {
        var added = StayLifecycleKit.Rooms(6);
        var expired = StayLifecycleKit.SettleIndexed(added, Free.RoomTypes, true);
        var lastVisible = expired.Where(r => !r.Hidden).OrderByDescending(r => r.Index).First();
        var after = StayLifecycleKit.SettleIndexed(expired.Where(r => r.Id != lastVisible.Id).ToList(), Free.RoomTypes, true);
        // Rooms 5 and 6 were visible, 6 is deleted: room 4 (the newest hidden one) takes the free slot.
        Assert.Equal([added[3].Id, added[4].Id], StayLifecycleKit.VisibleIds(after));
        Assert.Equal(3, after.Count(r => r.Hidden));
        Assert.Equal(5, after.Count);
    }

    [Fact]
    public void Rooms_owner_choice_is_never_reshuffled_by_a_later_pass_and_plus_brings_everything_back()
    {
        var added = StayLifecycleKit.Rooms(4);
        var expired = StayLifecycleKit.SettleIndexed(added, Free.RoomTypes, true);
        // The owner picks rooms 1 and 2 to stay active (Choose what stays).
        var chosen = StayLifecycleKit.Apply(expired, PlanRowChoice.OwnerIndexed(expired, new HashSet<Guid> { added[0].Id, added[1].Id }));
        Assert.Equal([added[0].Id, added[1].Id], StayLifecycleKit.VisibleIds(chosen));
        Assert.Empty(PlanRowChoice.Changed(chosen, PlanRowChoice.ChooseIndexed(chosen, Free.RoomTypes, true)));
        var plus = StayLifecycleKit.SettleIndexed(chosen, Plus.RoomTypes, true);
        Assert.DoesNotContain(plus, r => r.Hidden);
        Assert.Equal(4, plus.Count);
    }

    // ---------------------------------------------------------------- photos: the FIRST photos stay, cover first

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void Photos_expire_keeps_the_first_photos_and_never_moves_the_cover(int count)
    {
        var added = StayLifecycleKit.Photos(count);
        var ids = added.Select(p => p.Id).ToList();
        var keep = Math.Min(Free.Photos, count);

        Assert.Empty(PlanRowChoice.Changed(added, PlanRowChoice.ChoosePhotos(added, Plus.Photos, Plus.PaymentEnabled)));

        var expired = StayLifecycleKit.SettlePhotos(added, Free.Photos, Free.PaymentEnabled);
        Assert.Equal(count, expired.Count);
        Assert.Equal(ids.Take(keep).ToList(), StayLifecycleKit.VisibleIds(expired));
        // PhotoOrder is never rewritten: the cover stays the cover in every state.
        Assert.Equal(StayLifecycleKit.ById(added).Select(p => p.Index).ToList(), StayLifecycleKit.ById(expired).Select(p => p.Index).ToList());
        Assert.Equal(ids[0], StayLifecycleKit.VisibleIds(expired)[0]);
        Assert.Empty(PlanRowChoice.Changed(expired, PlanRowChoice.ChoosePhotos(expired, Free.Photos, true)));

        var back = StayLifecycleKit.SettlePhotos(expired, Plus.Photos, Plus.PaymentEnabled);
        Assert.DoesNotContain(back, p => p.Hidden);
        Assert.Equal(ids, StayLifecycleKit.InOrder(back));

        var again = StayLifecycleKit.SettlePhotos(back, Free.Photos, true);
        Assert.Equal(StayLifecycleKit.ById(expired), StayLifecycleKit.ById(again));

        var off = StayLifecycleKit.SettlePhotos(expired, Off.Photos, Off.PaymentEnabled);
        Assert.DoesNotContain(off, p => p.Hidden);
    }

    [Fact]
    public void Photos_delete_of_the_cover_while_inactive_promotes_the_next_photos_in_order()
    {
        var added = StayLifecycleKit.Photos(5);
        var expired = StayLifecycleKit.SettlePhotos(added, Free.Photos, true);
        var after = StayLifecycleKit.SettlePhotos(expired.Where(p => p.Id != added[0].Id).ToList(), Free.Photos, true);
        Assert.Equal([added[1].Id, added[2].Id], StayLifecycleKit.VisibleIds(after));
        Assert.Equal(2, after.Count(p => p.Hidden));
        Assert.Equal(4, after.Count);
    }

    [Fact]
    public void Photos_owner_choice_sets_the_cover_and_a_later_pass_keeps_it()
    {
        var added = StayLifecycleKit.Photos(5);
        var expired = StayLifecycleKit.SettlePhotos(added, Free.Photos, true);
        var picked = PlanRowChoice.OwnerPhotos(expired, [added[3].Id, added[0].Id]);
        var after = StayLifecycleKit.Apply(expired, picked);
        Assert.Equal([added[3].Id, added[0].Id], StayLifecycleKit.VisibleIds(after));
        Assert.Empty(PlanRowChoice.Changed(after, PlanRowChoice.ChoosePhotos(after, Free.Photos, true)));
        var all = added.Select(p => p.Id).ToList();
        Assert.True(PlanRowChoice.ValidOwnerChoice(all, [added[3].Id, added[0].Id], Free.Photos));
        Assert.False(PlanRowChoice.ValidOwnerChoice(all, [added[3].Id], Free.Photos));
    }

    // ---------------------------------------------------------------- jobs: newest stay Active, the rest Inactive

    [Fact]
    public void Jobs_expire_keeps_the_newest_active_and_resubscribe_restores_all_every_cycle()
    {
        var open = StayLifecycleKit.Jobs(4);
        Assert.All(StayLifecycleKit.SettleJobs(open, Plus.JobsPosts, Plus.PaymentEnabled), j => Assert.False(j.Inactive));

        var expired = StayLifecycleKit.SettleJobs(open, Free.JobsPosts, Free.PaymentEnabled);
        Assert.Equal([open[3].Id], expired.Where(j => !j.Inactive).Select(j => j.Id).ToList());
        Assert.Equal(3, expired.Count(j => j.Inactive));
        Assert.Equal(4, expired.Count);

        // Second identical pass: nothing flips.
        var map = PlanJobChoice.Inactive(expired, Free.JobsPosts, true);
        Assert.All(expired, j => Assert.Equal(j.Inactive, map[j.Id]));

        var back = StayLifecycleKit.SettleJobs(expired, Plus.JobsPosts, Plus.PaymentEnabled);
        Assert.All(back, j => Assert.False(j.Inactive));
        Assert.Equal(expired.Select(j => j.Id).ToList(), back.Select(j => j.Id).ToList());

        var again = StayLifecycleKit.SettleJobs(back, Free.JobsPosts, true);
        Assert.Equal(expired.Select(j => (j.Id, j.Inactive)).ToList(), again.Select(j => (j.Id, j.Inactive)).ToList());

        // L14: OFF never makes a job inactive and brings the inactive ones back.
        Assert.All(StayLifecycleKit.SettleJobs(expired, Off.JobsPosts, Off.PaymentEnabled), j => Assert.False(j.Inactive));
        Assert.All(StayLifecycleKit.SettleJobs(open, 1, hide: false), j => Assert.False(j.Inactive));
    }

    [Fact]
    public void Jobs_closing_the_active_job_on_free_makes_the_newest_inactive_job_active_at_once()
    {
        var expired = StayLifecycleKit.SettleJobs(StayLifecycleKit.Jobs(4), Free.JobsPosts, true);
        var activeId = expired.Single(j => !j.Inactive).Id;
        var openAfterClose = expired.Where(j => j.Id != activeId).ToList();   // a closed job leaves the open set
        var after = StayLifecycleKit.SettleJobs(openAfterClose, Free.JobsPosts, true);
        Assert.Equal(1, after.Count(j => !j.Inactive));
        Assert.Equal(openAfterClose.OrderByDescending(j => j.CreatedAt).First().Id, after.Single(j => !j.Inactive).Id);
    }

    [Fact]
    public void Jobs_inactive_job_is_kept_and_closed_jobs_are_not_in_the_pass()
    {
        Assert.Contains(StayJobStatuses.Inactive, StayJobStatuses.All);
        Assert.Contains("\"Status\" IN ('Active', 'Inactive')", PlanRows.OpenJobsSql);
        Assert.DoesNotContain("Closed", PlanRows.OpenJobsSql);
    }

    // ---------------------------------------------------------------- special prices: the last added stay live

    [Fact]
    public void Special_prices_expire_keeps_the_last_added_live_and_resubscribe_restores_all_every_cycle()
    {
        var rules = StayLifecycleKit.Rules(5);
        Assert.All(StayLifecycleKit.SettleRules(rules, Plus.SpecialPrices, Plus.PaymentEnabled), r => Assert.False(r.PausedByPlan));

        var expired = StayLifecycleKit.SettleRules(rules, Free.SpecialPrices, Free.PaymentEnabled);
        Assert.Equal([rules[4].Id], expired.Where(r => !r.PausedByPlan).Select(r => r.Id).ToList());
        Assert.Equal(4, expired.Count(r => r.PausedByPlan));
        Assert.Equal(5, expired.Count);

        var map = PlanRuleChoice.PausedByPlan(expired, Free.SpecialPrices, true);
        Assert.All(expired, r => Assert.Equal(r.PausedByPlan, map[r.Id]));

        var back = StayLifecycleKit.SettleRules(expired, Plus.SpecialPrices, Plus.PaymentEnabled);
        Assert.All(back, r => Assert.False(r.PausedByPlan));

        var again = StayLifecycleKit.SettleRules(back, Free.SpecialPrices, true);
        Assert.Equal(expired.Select(r => (r.Id, r.PausedByPlan)).ToList(), again.Select(r => (r.Id, r.PausedByPlan)).ToList());

        Assert.All(StayLifecycleKit.SettleRules(expired, Off.SpecialPrices, Off.PaymentEnabled), r => Assert.False(r.PausedByPlan));
    }

    [Fact]
    public void Special_prices_owner_swap_survives_the_next_pass_and_a_delete_refills_the_freed_slot()
    {
        var rules = StayLifecycleKit.Rules(3);
        var expired = StayLifecycleKit.SettleRules(rules, Free.SpecialPrices, true);
        // Make the oldest live (P6): it swaps with the live one.
        var swapped = expired.Select(r => r.Id == rules[0].Id ? r with { PausedByPlan = false } : r with { PausedByPlan = true }).ToList();
        var next = StayLifecycleKit.SettleRules(swapped, Free.SpecialPrices, true);
        Assert.Equal([rules[0].Id], next.Where(r => !r.PausedByPlan).Select(r => r.Id).ToList());

        // Delete the live one: the newest paused rule takes the slot (FB-18).
        var afterDelete = StayLifecycleKit.SettleRules(next.Where(r => r.Id != rules[0].Id).ToList(), Free.SpecialPrices, true);
        Assert.Equal([rules[2].Id], afterDelete.Where(r => !r.PausedByPlan).Select(r => r.Id).ToList());
    }

    [Fact]
    public void Special_prices_plan_paused_rule_refuses_edit_and_pause_with_plan_row_inactive()
    {
        var src = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be2", "StaySpecialPriceService.cs");
        Assert.Contains("if (row.PausedByPlan) return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.PlanRowInactive);", src);
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.PlanRowInactive));
    }

    // ---------------------------------------------------------------- a plan pass never deletes a row or touches the unflagged features

    [Fact]
    public void Plan_passes_never_delete_a_stay_row_and_never_touch_blocks_calendar_entries_or_reminder_flags()
    {
        var reconcile = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanLimitsReconcile.cs");
        var rows = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanRows.cs");
        foreach (var src in new[] { reconcile, rows })
        {
            Assert.DoesNotContain("DELETE FROM \"StayRooms\"", src);
            Assert.DoesNotContain("DELETE FROM \"StayPhotos\"", src);
            Assert.DoesNotContain("DELETE FROM \"StayJobs\"", src);
            Assert.DoesNotContain("DELETE FROM \"StaySpecialPrices\"", src);
            Assert.DoesNotContain("StayBlockedRanges", src);
            Assert.DoesNotContain("CalendarEntries", src);
            Assert.DoesNotContain("RemindersOn", src);
        }
    }

    // Plan effect counts (the grant clears the plan notice only when this is 0): paused special prices count like hidden rooms.
    [Fact]
    public void Plan_effect_counts_paused_special_prices_with_the_hidden_rooms_photos_and_inactive_jobs()
    {
        var reconcile = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanLimitsReconcile.cs");
        Assert.Contains("PlanRows.ApplySpecialPricesEffectAsync(db, listing.Id, limits.SpecialPrices, hide, today, ct)", reconcile);
        Assert.Contains("+ jobs.InactiveBefore + rules.PausedBefore;", reconcile);
        Assert.Contains("+ jobs.InactiveAfter + rules.PausedAfter;", reconcile);
    }
}
