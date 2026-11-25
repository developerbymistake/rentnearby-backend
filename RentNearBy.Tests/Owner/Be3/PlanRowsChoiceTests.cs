using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;

namespace RentNearBy.Tests.Owner.Be3;

// Contract 3.3 / 3.4 and the plan rule (after Plus ends the last added stay): the server choice of visible rows (cover and next
// photo kept, the last added rooms and routes and special prices, the owner's choice never reshuffled, everything back on Plus in
// the original order, nothing hidden while payment is OFF) and the index bands.
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
    public void Rooms_keep_the_last_added_and_older_rows_move_to_the_band()
    {
        var rooms = Enumerable.Range(1, 4).Select(i => R(i)).ToList();
        var targets = PlanRowChoice.ChooseIndexed(rooms, limit: 2, hide: true);
        Assert.Equal([(true, 101), (true, 102), (false, 1), (false, 2)], rooms.Select(r => targets.Single(t => t.Id == r.Id)).Select(t => (t.Hidden, t.Index)));
    }

    [Fact]
    public void Routes_keep_the_last_added_one_on_free()
    {
        var routes = Enumerable.Range(1, 5).Select(i => R(i)).ToList();
        var targets = PlanRowChoice.ChooseIndexed(routes, limit: 1, hide: true);
        Assert.Equal(routes[4].Id, targets.Single(t => !t.Hidden).Id);
        Assert.Equal([101, 102, 103, 104], routes.Take(4).Select(r => targets.Single(t => t.Id == r.Id).Index));
    }

    [Fact]
    public void Expire_then_plus_again_restores_every_row_in_the_order_they_were_added()
    {
        var rooms = Enumerable.Range(1, 6).Select(i => R(i)).ToList();
        var expired = PlanRowChoice.ChooseIndexed(rooms, limit: 2, hide: true);
        var afterExpiry = rooms.Select(r => expired.Single(t => t.Id == r.Id)).Select(t => new PlanRow(t.Id, t.Hidden, t.Index)).ToList();
        Assert.Equal([false, false], afterExpiry.Skip(4).Select(r => r.Hidden));

        var plus = PlanRowChoice.ChooseIndexed(afterExpiry, limit: 6, hide: true);
        Assert.All(plus, t => Assert.False(t.Hidden));
        Assert.Equal([1, 2, 3, 4, 5, 6], rooms.Select(r => plus.Single(t => t.Id == r.Id).Index));
        Assert.Empty(PlanRowChoice.Changed(rooms, plus));
    }

    [Fact]
    public void A_freed_slot_on_free_brings_back_the_newest_hidden_row()
    {
        // Rooms 1..4 were hidden after Plus ended (101..104), 5 and 6 stay; the owner removed 6, so one slot is free.
        var five = R(1);
        var hidden = Enumerable.Range(101, 4).Select(i => R(i, true)).ToList();
        var targets = PlanRowChoice.ChooseIndexed([five, .. hidden], limit: 2, hide: true);
        Assert.False(targets.Single(t => t.Id == hidden[3].Id).Hidden);
        Assert.Equal((false, 1), targets.Where(t => t.Id == hidden[3].Id).Select(t => (t.Hidden, t.Index)).Single());
        Assert.Equal((false, 2), targets.Where(t => t.Id == five.Id).Select(t => (t.Hidden, t.Index)).Single());
        Assert.Equal([101, 102, 103], hidden.Take(3).Select(r => targets.Single(t => t.Id == r.Id).Index));
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
    public void Plus_again_brings_every_row_back_older_hidden_rows_first()
    {
        var rows = new[] { R(1), R(2), R(101, true), R(102, true) };
        var targets = PlanRowChoice.ChooseIndexed(rows, limit: 6, hide: true);
        Assert.All(targets, t => Assert.False(t.Hidden));
        Assert.Equal([3, 4, 1, 2], rows.Select(r => targets.Single(t => t.Id == r.Id).Index));
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
    public void Special_prices_keep_the_last_added_live_and_never_undo_the_owners_swap()
    {
        var created = new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
        var early = new PlanRuleRow(Guid.NewGuid(), false, created);
        var late = new PlanRuleRow(Guid.NewGuid(), false, created.AddHours(3));
        var paused = PlanRuleChoice.PausedByPlan([early, late], limit: 1, hide: true);
        Assert.False(paused[late.Id]);
        Assert.True(paused[early.Id]);

        // The owner made the older one live (P6): it ranks first now and stays live.
        var swapped = PlanRuleChoice.PausedByPlan([early with { PausedByPlan = false }, late with { PausedByPlan = true }], 1, true);
        Assert.False(swapped[early.Id]);
        Assert.True(swapped[late.Id]);
        Assert.All(PlanRuleChoice.PausedByPlan([early, late], 1, hide: false).Values, Assert.False);
    }

    [Fact]
    public void Set_based_sql_ranks_like_the_pure_choice()
    {
        var cte = "pv (\"PlanId\") AS (VALUES ('00000000-0000-0000-0000-000000000000'::uuid))";
        Assert.Contains("ORDER BY r.\"HiddenByPlan\", r.\"RoomIndex\" DESC, r.\"Id\"", PlanLimitsReconcile.StayRoomsBatchSql(cte));
        Assert.Contains("PARTITION BY l, rn <= n ORDER BY h DESC, ix, \"Id\"", PlanLimitsReconcile.StayRoomsBatchSql(cte));
        Assert.Contains($"{PlanRowBands.HiddenBase} + k.pos", PlanLimitsReconcile.StayRoomsBatchSql(cte));
        Assert.Contains("ORDER BY p.\"HiddenByPlan\", p.\"PhotoOrder\", p.\"Id\"", PlanLimitsReconcile.StayPhotosBatchSql(cte));
        Assert.Contains("ORDER BY sp.\"PausedByPlan\", sp.\"CreatedAt\" DESC, sp.\"Id\"", PlanLimitsReconcile.StayRulesBatchSql(cte));
        Assert.Contains("ORDER BY r.\"HiddenByPlan\", r.\"RouteIndex\" DESC, r.\"Id\"", PlanLimitsReconcile.TaxiRoutesBatchSql(cte));
        Assert.Contains("PARTITION BY l, rn <= n ORDER BY h DESC, ix, \"Id\"", PlanLimitsReconcile.TaxiRoutesBatchSql(cte));
        Assert.Contains("ORDER BY j.\"CreatedAt\" DESC, j.\"Id\"", PlanLimitsReconcile.StayJobsBatchSql(cte));
        Assert.Contains("SELECT \"Id\", \"PausedByPlan\", \"CreatedAt\" FROM \"StaySpecialPrices\"", Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanRows.cs"));
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
