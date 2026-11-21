using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;

namespace RentNearBy.Tests.Owner.Be3;

// Contract 3.3 / 3.4: the server choice of visible rows (cover and next photo kept, lowest index rooms and routes, the owner's
// choice never reshuffled, everything back on Plus, nothing hidden while payment is OFF) and the index bands.
public class PlanRowsChoiceTests
{
    private static PlanRow R(int index, bool hidden = false) => new(Guid.NewGuid(), hidden, index);

    [Fact]
    public void Free_keeps_the_cover_and_the_next_photo_and_hides_the_rest()
    {
        var photos = Enumerable.Range(0, 5).Select(i => R(i)).ToList();
        var targets = PlanRowChoice.ChoosePhotos(photos, limit: 2, hide: true);
        Assert.Equal([false, false, true, true, true], photos.Select(p => targets.Single(t => t.Id == p.Id).Hidden));
        Assert.All(targets, t => Assert.Equal(photos.Single(p => p.Id == t.Id).Index, t.Index));
    }

    [Fact]
    public void Rooms_keep_the_lowest_indexes_and_hidden_rows_move_to_the_band()
    {
        var rooms = Enumerable.Range(1, 4).Select(i => R(i)).ToList();
        var targets = PlanRowChoice.ChooseIndexed(rooms, limit: 2, hide: true);
        Assert.Equal([(false, 1), (false, 2), (true, 101), (true, 102)], rooms.Select(r => targets.Single(t => t.Id == r.Id)).Select(t => (t.Hidden, t.Index)));
    }

    [Fact]
    public void The_owners_choice_is_never_reshuffled()
    {
        // The owner kept rooms B and D visible (1, 2); A and C are hidden (101, 102). The daily pass changes nothing.
        var b = R(1); var d = R(2); var a = R(101, true); var c = R(102, true);
        var rows = new[] { a, b, c, d };
        var targets = PlanRowChoice.ChooseIndexed(rows, limit: 2, hide: true);
        Assert.Empty(PlanRowChoice.Changed(rows, targets));
    }

    [Fact]
    public void Plus_again_brings_every_row_back_lowest_hidden_first()
    {
        var rows = new[] { R(1), R(2), R(101, true), R(102, true) };
        var targets = PlanRowChoice.ChooseIndexed(rows, limit: 6, hide: true);
        Assert.All(targets, t => Assert.False(t.Hidden));
        Assert.Equal([1, 2, 3, 4], rows.Select(r => targets.Single(t => t.Id == r.Id).Index));
    }

    [Fact]
    public void Payment_off_hides_nothing_and_clears_old_flags()
    {
        var rows = new[] { R(1), R(2), R(3), R(101, true) };
        var targets = PlanRowChoice.ChooseIndexed(rows, limit: 2, hide: false);
        Assert.All(targets, t => Assert.False(t.Hidden));
        Assert.Equal(0, PlanRowChoice.HiddenCount(PlanRowChoice.ChoosePhotos(rows, 1, hide: false)));
    }

    [Fact]
    public void A_new_visible_row_after_a_full_replace_never_meets_the_hidden_band()
    {
        var targets = PlanRowChoice.ChooseIndexed(Enumerable.Range(1, OwnerLimits.RoomsCeiling).Select(i => R(i)), 2, true);
        Assert.True(targets.Where(t => !t.Hidden).Max(t => t.Index) < PlanRowBands.HiddenBase);
        Assert.True(OwnerLimits.RoomsCeiling + 1 < PlanRowBands.HiddenBase && OwnerLimits.RoutesCeiling + 1 < PlanRowBands.HiddenBase);
    }

    [Fact]
    public void Special_prices_keep_the_soonest_ending_live_and_never_undo_the_owners_swap()
    {
        var today = new DateOnly(2026, 11, 21);
        var early = new PlanRuleRow(Guid.NewGuid(), false, today.AddDays(5));
        var late = new PlanRuleRow(Guid.NewGuid(), false, today.AddDays(20));
        var paused = PlanRuleChoice.PausedByPlan([early, late], limit: 1, hide: true);
        Assert.False(paused[early.Id]);
        Assert.True(paused[late.Id]);

        // The owner made the late one live (P6): it ranks first now and stays live.
        var swapped = PlanRuleChoice.PausedByPlan([early with { PausedByPlan = true }, late with { PausedByPlan = false }], 1, true);
        Assert.False(swapped[late.Id]);
        Assert.True(swapped[early.Id]);
        Assert.All(PlanRuleChoice.PausedByPlan([early, late], 1, hide: false).Values, Assert.False);
    }

    [Fact]
    public void Set_based_sql_ranks_like_the_pure_choice()
    {
        var cte = "pv (\"PlanId\") AS (VALUES ('00000000-0000-0000-0000-000000000000'::uuid))";
        Assert.Contains("ORDER BY r.\"HiddenByPlan\", r.\"RoomIndex\", r.\"Id\"", PlanLimitsReconcile.StayRoomsBatchSql(cte));
        Assert.Contains($"{PlanRowBands.HiddenBase} + r.rn - r.n", PlanLimitsReconcile.StayRoomsBatchSql(cte));
        Assert.Contains("ORDER BY p.\"HiddenByPlan\", p.\"PhotoOrder\", p.\"Id\"", PlanLimitsReconcile.StayPhotosBatchSql(cte));
        Assert.Contains("ORDER BY sp.\"PausedByPlan\", sp.\"DateTo\", sp.\"Id\"", PlanLimitsReconcile.StayRulesBatchSql(cte));
        Assert.Contains("ORDER BY r.\"HiddenByPlan\", r.\"RouteIndex\", r.\"Id\"", PlanLimitsReconcile.TaxiRoutesBatchSql(cte));
        Assert.Contains("CASE WHEN @hide THEN", PlanLimitsReconcile.TaxiRoutesBatchSql(cte));
    }

    [Fact]
    public void Hidden_routes_close_offers_with_the_plan_reason_and_never_count_as_a_miss()
    {
        Assert.Equal("This route is paused on Free.", RentNearBy.Core.Models.TaxiRequests.OfferEndReasons.RingEndBody(RentNearBy.Core.Models.TaxiRequests.OfferEndReasons.RoutePausedByPlan));
        var reconcile = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanLimitsReconcile.cs");
        Assert.Contains("closure.RoutesChangedAsync(db, listing.Id, visible, ct, OfferEndReasons.RoutePausedByPlan)", reconcile);
        var visible = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanVisibleService.cs");
        Assert.Contains("OfferEndReasons.RoutePausedByPlan", visible);
        // Lock order 4.1: the closure (request rows, ring lock) runs before the listing row lock.
        Assert.True(reconcile.IndexOf("closure.RoutesChangedAsync", StringComparison.Ordinal) < reconcile.IndexOf("OwnerLocks.LockTaxiAsync", StringComparison.Ordinal));
    }
}
