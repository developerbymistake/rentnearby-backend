using RentNearBy.Core.Models.Owner;
using RentNearBy.Tests.Owner.Be3;

namespace RentNearBy.Tests.Owner.Subscription;

// Subscription matrix L3-L14 for BOTH roles: a Play state (what the sync sees) -> entitled or not -> the limits the server computes
// -> the rows the reconcile keeps. Pure; the transaction side is pinned by source guards.
public class BillingLifecycleTests
{
    public static TheoryData<string> Kinds => new() { ListingKinds.Stay, ListingKinds.Taxi };

    private static readonly DateTime Future = DateTime.UtcNow.AddDays(10);
    private static readonly DateTime Past = DateTime.UtcNow.AddDays(-1);

    private static PlanLimits Limits(string kind, string state, DateTime? expiry, bool paymentOn = true, string source = UserPlanSources.Subscription)
        => Be3Kit.Compute(kind, paymentOn, PlayStates.IsEntitled(state, expiry) ? Be3Kit.PlusId(kind) : null, source);

    private static int Cap(string kind, PlanLimits l) => kind == ListingKinds.Stay ? l.RoomTypes : l.Routes;

    private static List<PlanRow> Rows(int n) => Enumerable.Range(1, n).Select(i => new PlanRow(Guid.NewGuid(), false, i)).ToList();

    private static List<PlanRow> Reconcile(string kind, IReadOnlyList<PlanRow> rows, PlanLimits l)
    {
        var map = PlanRowChoice.ChooseIndexed(rows, Cap(kind, l), l.PaymentEnabled).ToDictionary(t => t.Id);
        return rows.Select(r => new PlanRow(r.Id, map[r.Id].Hidden, map[r.Id].Index)).ToList();
    }

    private static int Visible(IEnumerable<PlanRow> rows) => rows.Count(r => !r.Hidden);

    [Theory, MemberData(nameof(Kinds))]
    public void Grant_active_subscription_is_plus_and_free_is_not(string kind)
    {
        var plus = Limits(kind, PlayStates.Active, Future);
        var free = Limits(kind, PlayStates.Expired, Past);

        Assert.True(plus.IsPaid);
        Assert.False(free.IsPaid);
        Assert.True(Cap(kind, plus) > Cap(kind, free));
        Assert.True(plus.CalendarEntries > free.CalendarEntries);
        Assert.False(plus.CanUpgrade);
        Assert.True(free.CanUpgrade);
    }

    [Theory]
    [InlineData(PlayStates.Active, true)]
    [InlineData(PlayStates.InGracePeriod, true)]
    [InlineData(PlayStates.Canceled, true)]
    [InlineData(PlayStates.OnHold, false)]
    [InlineData(PlayStates.Paused, false)]
    [InlineData(PlayStates.Expired, false)]
    [InlineData(PlayStates.Pending, false)]
    [InlineData(PlayStates.PendingPurchaseCanceled, false)]
    public void Every_play_state_keeps_or_drops_plus_by_the_one_rule_for_both_roles(string state, bool plus)
    {
        foreach (var kind in new[] { ListingKinds.Stay, ListingKinds.Taxi })
        {
            var rows = Reconcile(kind, Rows(8), Limits(kind, state, Future));
            var cap = Cap(kind, Limits(kind, state, Future));
            Assert.Equal(plus, Limits(kind, state, Future).IsPaid);
            Assert.Equal(Math.Min(8, cap), Visible(rows));
            Assert.Equal(8, rows.Count);
        }
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Cancel_keeps_plus_until_the_end_date_then_drops_it(string kind)
    {
        Assert.True(Limits(kind, PlayStates.Canceled, Future).IsPaid);
        Assert.False(Limits(kind, PlayStates.Canceled, Past).IsPaid);
        Assert.False(Limits(kind, PlayStates.Canceled, null).IsPaid);
        Assert.Equal(OwnerPlanCardStates.Cancelled, OwnerPlanCardStates.Of(true, UserPlanSources.Subscription, PlayStates.Canceled, false, false));
        Assert.Equal(OwnerPlanCardStates.Ended, OwnerPlanCardStates.Of(true, null, PlayStates.Expired, false, true));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Grace_stays_plus_with_its_own_card_state_and_hold_pause_read_on_hold(string kind)
    {
        Assert.Equal(Cap(kind, Limits(kind, PlayStates.Active, Future)), Cap(kind, Limits(kind, PlayStates.InGracePeriod, Future)));
        Assert.Equal(OwnerPlanCardStates.Grace, OwnerPlanCardStates.Of(true, UserPlanSources.Subscription, PlayStates.InGracePeriod, true, false));
        Assert.Equal(OwnerPlanCardStates.OnHold, OwnerPlanCardStates.Of(true, null, PlayStates.OnHold, true, false));
        Assert.Equal(OwnerPlanCardStates.OnHold, OwnerPlanCardStates.Of(true, null, PlayStates.Paused, true, false));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Hold_then_recovery_hides_then_restores_the_same_rows(string kind)
    {
        var start = Rows(Math.Min(6, Cap(kind, Limits(kind, PlayStates.Active, Future))));
        var active = Reconcile(kind, start, Limits(kind, PlayStates.Active, Future));
        var hold = Reconcile(kind, active, Limits(kind, PlayStates.OnHold, Future));
        var back = Reconcile(kind, hold, Limits(kind, PlayStates.Active, Future));

        Assert.Equal(Math.Min(6, Cap(kind, Limits(kind, PlayStates.Active, Future))), Visible(active));
        Assert.True(Visible(hold) < Visible(active));
        Assert.Equal(Visible(active), Visible(back));
        Assert.Equal(start.Select(r => r.Id), back.OrderBy(r => r.Index).Select(r => r.Id));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Pause_and_resume_behave_like_hold(string kind)
    {
        var start = Reconcile(kind, Rows(6), Limits(kind, PlayStates.Active, Future));
        var paused = Reconcile(kind, start, Limits(kind, PlayStates.Paused, Future));
        var resumed = Reconcile(kind, paused, Limits(kind, PlayStates.Active, Future));
        Assert.Equal(Cap(kind, Limits(kind, PlayStates.Expired, Past)), Visible(paused));
        Assert.Equal(Visible(start), Visible(resumed));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Expire_then_resubscribe_loses_nothing_and_keeps_the_order(string kind)
    {
        var start = Rows(Math.Min(6, Cap(kind, Limits(kind, PlayStates.Active, Future))));
        var expired = Reconcile(kind, start, Limits(kind, PlayStates.Expired, Past));
        var again = Reconcile(kind, expired, Limits(kind, PlayStates.Active, Future));

        Assert.Equal(start.Count, expired.Count);
        Assert.Equal(Cap(kind, Limits(kind, PlayStates.Expired, Past)), Visible(expired));
        Assert.Equal(start.Select(r => r.Id), again.OrderBy(r => r.Index).Select(r => r.Id));
        Assert.Equal(Math.Min(6, Cap(kind, Limits(kind, PlayStates.Active, Future))), Visible(again));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Revoke_drops_plus_at_once_and_a_later_rebuy_restores(string kind)
    {
        var plus = Reconcile(kind, Rows(6), Limits(kind, PlayStates.Active, Future));
        var revoked = Reconcile(kind, plus, Limits(kind, PlayStates.Expired, Future));
        var rebuy = Reconcile(kind, revoked, Limits(kind, PlayStates.Active, Future));

        Assert.False(Limits(kind, PlayStates.Expired, Future).IsPaid);
        Assert.True(Visible(revoked) < Visible(plus));
        Assert.Equal(Visible(plus), Visible(rebuy));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Trial_start_is_plus_and_its_end_is_free(string kind)
    {
        var trial = Limits(kind, PlayStates.Active, Future, source: UserPlanSources.Trial);
        Assert.True(trial.IsPaid);
        Assert.Equal(UserPlanSources.Trial, trial.Source);
        Assert.Equal(OwnerPlanCardStates.Trial, OwnerPlanCardStates.Of(true, UserPlanSources.Trial, null, true, false));
        Assert.Equal(OwnerPlanCardStates.Trial, OwnerPlanCardStates.Of(true, UserPlanSources.Subscription, PlayStates.Active, true, false, inPlayTrial: true));
        Assert.False(Limits(kind, PlayStates.Expired, Past).IsPaid);
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Switch_cycle_and_deferred_change_never_hide_a_row(string kind)
    {
        var rows = Reconcile(kind, Rows(Cap(kind, Limits(kind, PlayStates.Active, Future))), Limits(kind, PlayStates.Active, Future));
        var other = Reconcile(kind, rows, Limits(kind, PlayStates.Active, DateTime.UtcNow.AddDays(100)));
        Assert.Equal(rows.Count, Visible(other));
        Assert.Empty(PlanRowChoice.Changed(rows, PlanRowChoice.ChooseIndexed(other, Cap(kind, Limits(kind, PlayStates.Active, Future)), true)));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Restore_and_the_same_notification_twice_change_nothing(string kind)
    {
        var l = Limits(kind, PlayStates.Expired, Past);
        var once = Reconcile(kind, Rows(6), l);
        var twice = Reconcile(kind, once, l);
        Assert.Empty(PlanRowChoice.Changed(once, PlanRowChoice.ChooseIndexed(twice, Cap(kind, l), l.PaymentEnabled)));

        var plus = Limits(kind, PlayStates.Active, Future);
        var a = Reconcile(kind, once, plus);
        var b = Reconcile(kind, a, plus);
        Assert.Equal(a.Select(r => (r.Id, r.Hidden, r.Index)), b.Select(r => (r.Id, r.Hidden, r.Index)));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Out_of_order_notifications_end_on_the_state_of_the_latest_snapshot(string kind)
    {
        var rows = Rows(6);
        var inOrder = Reconcile(kind, Reconcile(kind, rows, Limits(kind, PlayStates.Active, Future)), Limits(kind, PlayStates.Expired, Past));
        var onlyExpired = Reconcile(kind, Reconcile(kind, rows, Limits(kind, PlayStates.Expired, Past)), Limits(kind, PlayStates.Expired, Past));
        Assert.Equal(inOrder.Where(r => !r.Hidden).Select(r => r.Id), onlyExpired.Where(r => !r.Hidden).Select(r => r.Id));
    }

    [Fact]
    public void Rtdn_types_never_decide_state_the_snapshot_does()
    {
        var sync = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "PlayBillingSyncService.cs");
        Assert.Contains("snapshot.IsEntitled", sync);
        Assert.Contains("await slots.ExpireEffectsAsync(row.UserId, row.FeatureKey);", sync);
        Assert.Equal(13, PlayRtdnTypes.Expired);
        Assert.Equal(12, PlayRtdnTypes.Revoked);
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Payment_off_never_hides_in_any_state(string kind)
    {
        foreach (var state in new[] { PlayStates.Active, PlayStates.OnHold, PlayStates.Expired, PlayStates.Canceled, PlayStates.Paused })
        {
            var l = Limits(kind, state, Future, paymentOn: false);
            Assert.False(l.PaymentEnabled);
            Assert.False(l.IsPaid);
            Assert.False(l.CanUpgrade);
            Assert.All(Reconcile(kind, Rows(8), l), r => Assert.False(r.Hidden));
        }
        Assert.Equal(OwnerPlanCardStates.PaymentOff, OwnerPlanCardStates.Of(false, null, null, false, false));
        Assert.Equal(OwnerPlanCardStates.PaymentOff, OwnerPlanCardStates.Of(false, UserPlanSources.Subscription, PlayStates.Active, true, false));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Payment_off_to_on_hides_only_what_the_plan_does_not_cover(string kind)
    {
        var off = Reconcile(kind, Rows(8), Limits(kind, PlayStates.Expired, Past, paymentOn: false));
        var on = Reconcile(kind, off, Limits(kind, PlayStates.Expired, Past));
        Assert.Equal(Cap(kind, Limits(kind, PlayStates.Expired, Past)), Visible(on));
        Assert.Equal(8, on.Count);
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Admin_grant_keeps_ranking_while_payment_is_off_and_a_bought_plan_does_not(string kind)
    {
        var grant = Be3Kit.OffWithGrant(kind);
        var bought = Limits(kind, PlayStates.Active, Future, paymentOn: false);
        Assert.True(grant.PriorityRank > bought.PriorityRank);
        Assert.Equal(OwnerPlanCardStates.AdminGrant, OwnerPlanCardStates.Of(false, UserPlanSources.AdminGrant, null, false, false));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Detached_or_other_account_leaves_a_free_owner_untouched(string kind)
    {
        var free = Reconcile(kind, Rows(1), Limits(kind, PlayStates.Expired, Past));
        Assert.All(free, r => Assert.False(r.Hidden));
        Assert.Equal(OwnerPlanCardStates.Free, OwnerPlanCardStates.Of(true, null, null, false, false));
    }

    [Fact]
    public void Both_roles_share_the_one_reconcile_path_and_the_one_expiry_call()
    {
        var slots = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "PlanSlotService.cs");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(slots, "planRows.ApplyForOwnerAsync\\(userId, featureKey, limits\\)").Count);
        var recon = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanLimitsReconcile.cs");
        Assert.Contains("ListingKinds.Stay => await ApplyStayAsync", recon);
        Assert.Contains("ListingKinds.Taxi => await ApplyTaxiAsync", recon);
        Assert.Contains("var hide = limits.PaymentEnabled;", recon);
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Plan_limits_for_both_roles_are_the_seeded_free_and_plus_values(string kind)
    {
        var free = Limits(kind, PlayStates.Expired, Past);
        var plus = Limits(kind, PlayStates.Active, Future);
        Assert.Equal((4, 50), (free.CalendarEntries, plus.CalendarEntries));
        Assert.Equal((15, 183), (free.UpcomingDays, plus.UpcomingDays));
        Assert.Equal((0, 6), (free.MonthsAhead, plus.MonthsAhead));
        Assert.Equal((false, true), (free.MonthSummary, plus.MonthSummary));
        Assert.Equal((false, true), (free.Reminders, plus.Reminders));
        if (kind == ListingKinds.Stay)
        {
            Assert.Equal((2, 8, 2, 6), (free.Photos, plus.Photos, free.RoomTypes, plus.RoomTypes));
            Assert.Equal((1, 4, 1, 5), (free.JobsPosts, plus.JobsPosts, free.SpecialPrices, plus.SpecialPrices));
            Assert.Equal((1, 10), (free.BlockedRanges, plus.BlockedRanges));
        }
        else
        {
            Assert.Equal((1, 5, 1, 2), (free.Routes, plus.Routes, free.RingWeight, plus.RingWeight));
        }
    }
}
