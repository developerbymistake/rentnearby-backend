using RentNearBy.Api.Handlers;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;

namespace RentNearBy.Tests.Owner.Be3;

// Plan rule AFTER (owner order 2026-11-25): rows above the Free limit stay in the list as inactive, Delete only. A write to such a
// row answers 409 PlanRowInactive (rooms, routes, special prices, jobs, calendar entries); a hidden room, route or photo can be
// deleted on its own; calendar entries keep the last added active and the month read returns the others flagged.
public class PlanRowInactiveTests
{
    private static string Method(string src, string signature) => Between(src, signature, "\n    public static ");

    private static string Between(string src, string signature, string nextMember)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = src.IndexOf(nextMember, start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? src[start..] : src[start..next];
    }

    private static string Handlers(string file) => Be3Kit.Source("RentNearBy.Api", "Handlers", file);

    [Fact]
    public void Plan_row_inactive_is_a_409_with_the_plus_line()
    {
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.PlanRowInactive));
        Assert.Equal("This one is inactive on the Free plan. Get Plus to use it again.", OwnerErrorTypes.MessageOf(OwnerErrorTypes.PlanRowInactive));
        Assert.Contains(OwnerErrorTypes.PlanRowInactive, OwnerErrorTypes.All);
    }

    [Theory]
    [InlineData("StayEndpoints.cs", "group.MapDelete(\"/me/rooms/{roomId:guid}\", StayHandlers.DeleteHiddenRoom).RequireAuthorization(\"CanListStays\").OwnerWriteLimited();")]
    [InlineData("TaxiEndpoints.cs", "group.MapDelete(\"/me/routes/{routeId:guid}\", TaxiHandlers.DeleteHiddenRoute).RequireAuthorization(\"CanListTaxis\").OwnerWriteLimited();")]
    public void Hidden_room_and_route_delete_routes_are_owner_write_limited(string file, string line)
        => Assert.Contains(line, Be3Kit.Source("RentNearBy.Api", "Endpoints", file));

    [Fact]
    public void Hidden_room_delete_takes_the_locks_deletes_only_a_hidden_row_and_re_applies_the_plan()
    {
        Assert.Contains("AND \"HiddenByPlan\"", StayHandlers.DeleteHiddenRoomSql);
        var body = Method(Handlers("StayHandlers.cs"), "public static async Task<IResult> DeleteHiddenRoom(");
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("stay.Rooms.Any(r => r.Id == roomId)") < At("BeginTransactionAsync"));
        Assert.Contains("OwnerHttp.Refuse(OwnerErrorTypes.BadRequest, VisibleRoomDelete)", body);
        Assert.True(At("slots.LockOwnerAsync") < At("OwnerLocks.LockStayAsync"));
        Assert.True(At("OwnerLocks.LockStayAsync") < At("DeleteHiddenRoomSql"));
        Assert.True(At("DeleteHiddenRoomSql") < At("planRows.ApplyForOwnerAsync"));
        Assert.True(At("planRows.ApplyForOwnerAsync") < At("CommitTransactionAsync"));
        Assert.True(At("CommitTransactionAsync") < At("StayCache.InvalidateAsync"));
    }

    [Fact]
    public void Hidden_route_delete_takes_the_locks_and_deletes_only_a_hidden_row()
    {
        Assert.Contains("AND \"HiddenByPlan\"", TaxiHandlers.DeleteHiddenRouteSql);
        var body = Method(Handlers("TaxiHandlers.cs"), "public static async Task<IResult> DeleteHiddenRoute(");
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.Contains("OwnerHttp.Refuse(OwnerErrorTypes.BadRequest, VisibleRouteDelete)", body);
        Assert.True(At("slots.LockOwnerAsync") < At("OwnerLocks.LockTaxiAsync"));
        Assert.True(At("OwnerLocks.LockTaxiAsync") < At("DeleteHiddenRouteSql"));
        Assert.True(At("DeleteHiddenRouteSql") < At("CommitTransactionAsync"));
        Assert.True(At("CommitTransactionAsync") < At("TaxiCache.InvalidateAsync"));
        // A hidden route rings nothing: no closure and no plan pass that could hide a visible route under the listing lock.
        Assert.DoesNotContain("RoutesChangedAsync", body);
    }

    // L15: a delete that finds 0 hidden rows because a re-buy just restored the row says so (the app reloads), not 404.
    [Theory]
    [InlineData("StayHandlers.cs", "public static async Task<IResult> DeleteHiddenRoom(", "RoomActiveAgain")]
    [InlineData("TaxiHandlers.cs", "public static async Task<IResult> DeleteHiddenRoute(", "RouteActiveAgain")]
    public void Delete_of_a_row_a_rebuy_restored_answers_active_again(string file, string signature, string message)
    {
        var body = Method(Handlers(file), signature);
        Assert.Contains($"OwnerHttp.Refuse(OwnerErrorTypes.BadRequest, {message})", body);
        Assert.True(body.IndexOf("if (deleted == 0)", StringComparison.Ordinal) < body.IndexOf(message, StringComparison.Ordinal));
    }

    // L6: a route save that frees a slot on Free while routes are hidden brings the newest hidden one back in its own transaction.
    [Fact]
    public void Route_save_refills_hidden_routes_after_its_commit_under_the_owner_lock()
    {
        var body = Method(Handlers("TaxiHandlers.cs"), "public static async Task<IResult> UpsertRoutes(");
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("await unitOfWork.CommitTransactionAsync();") < At("RefillHiddenRoutesAsync"));
        var refill = Method(Handlers("TaxiHandlers.cs"), "public static async Task<bool> RefillHiddenRoutesAsync(");
        Assert.True(refill.IndexOf("slots.LockOwnerAsync", StringComparison.Ordinal) < refill.IndexOf("planRows.ApplyForOwnerAsync", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("StayHandlers.cs", "public static async Task<IResult> UpdateRoomPrice(", "HiddenRoomAsync")]
    [InlineData("TaxiHandlers.cs", "public static async Task<IResult> UpdateFare(", "HiddenRouteAsync")]
    public void Price_edit_of_a_hidden_row_answers_plan_row_inactive(string file, string signature, string check)
    {
        var body = Method(Handlers(file), signature);
        Assert.Contains(check, body);
        Assert.Contains("OwnerHttp.RowInactive()", body);
    }

    [Fact]
    public void Plan_paused_special_price_refuses_edit_and_pause_and_keeps_delete_and_make_live()
    {
        var src = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be2", "StaySpecialPriceService.cs");
        const string refuse = "if (row.PausedByPlan) return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.PlanRowInactive);";
        string Service(string name) => Between(src, name, "\n    public async Task");
        Assert.Contains(refuse, Service("public async Task<Be2Result<SpecialPriceWriteDto>> UpdateAsync("));
        Assert.Contains(refuse, Service("public async Task<Be2Result<SpecialPriceWriteDto>> PauseAsync("));
        Assert.DoesNotContain("PlanRowInactive", Service("public async Task<Be2Result<bool>> DeleteAsync("));
        Assert.DoesNotContain("PlanRowInactive", Service("public async Task<Be2Result<SpecialPriceWriteDto>> MakeLiveAsync("));
        Assert.Contains("p.\"PausedByPlan\", p.\"CreatedAt\" DESC, p.\"Id\") AS rk", src);
    }

    [Fact]
    public void Calendar_keeps_the_last_added_entries_active_on_every_reader()
    {
        Assert.Contains("ORDER BY m.\"CreatedAt\" DESC, m.\"Id\" LIMIT @limit", OwnerCalendarSql.VisibleManualSet);
        Assert.Contains("ORDER BY m.\"CreatedAt\" DESC, m.\"Id\" LIMIT @lim",
            Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "StayHubReader.cs"));
        Assert.Contains("ORDER BY v.\"CreatedAt\" DESC, v.\"Id\" LIMIT @manualLimit",
            Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be4", "OwnerHomeShared.cs"));
    }

    [Fact]
    public void Month_and_upcoming_return_inactive_entries_flagged_and_get_by_id_does_not()
    {
        Assert.Contains($"NOT {OwnerCalendarSql.Visible()} AS \"InactiveByPlan\"", OwnerCalendarSql.Month);
        Assert.Contains("c.\"DateTo\" >= @from AND c.\"DateFrom\" <= @to) x)", OwnerCalendarSql.Month);
        Assert.Contains("false AS \"InactiveByPlan\"", OwnerCalendarSql.OneVisible);
        foreach (var sql in new[] { OwnerCalendarSql.Upcoming, OwnerCalendarSql.UpcomingAfter })
        {
            Assert.Contains($"NOT {OwnerCalendarSql.Visible()} AS \"InactiveByPlan\"", sql);
            Assert.DoesNotContain("false AS \"InactiveByPlan\"", sql);
        }
    }

    [Fact]
    public void Upcoming_lists_an_inactive_manual_row_dated_after_the_window_so_it_can_be_deleted()
    {
        var v = OwnerCalendarSql.Visible();
        var branch = $"AND ((c.\"DateFrom\" <= @upcomingTo AND {v}) OR (c.\"Kind\" = 'Manual' AND NOT {v}))";
        Assert.Contains(branch, OwnerCalendarSql.Upcoming);
        Assert.Contains(branch, OwnerCalendarSql.UpcomingAfter);
        // Visible rows keep the window; only the above-limit manual rows pass it. Past rows are never inactive.
        Assert.Contains("c.\"DateTo\" >= @today", OwnerCalendarSql.Upcoming);
        Assert.Contains("(c.\"DateFrom\", c.\"Id\") > (@cd, @cid)", OwnerCalendarSql.UpcomingAfter);
        // Edit and money stay refused for such a row, Delete stays open.
        Assert.Contains(v, OwnerCalendarSql.Update);
        Assert.Contains(v, OwnerCalendarSql.Money);
        Assert.DoesNotContain("Visible", OwnerCalendarSql.Delete);
    }

    [Fact]
    public void Edit_and_money_of_an_inactive_entry_answer_plan_row_inactive()
    {
        var writer = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be1", "OwnerCalendarWriter.cs");
        Assert.Contains("ReadVisibleAsync(db, ctx, id, ct) == null) return Be1Error.Of(OwnerErrorTypes.PlanRowInactive);", writer);
        var money = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be1", "MoneyNoteService.cs");
        Assert.Contains("Be1Error.Of(OwnerErrorTypes.PlanRowInactive)", money);
        // Delete is unchanged: no visibility check.
        Assert.DoesNotContain("Visible", OwnerCalendarSql.Delete);
    }

    [Fact]
    public void Entry_dto_carries_the_inactive_flag()
    {
        var row = new CalendarEntryRow { Id = Guid.NewGuid(), Kind = CalendarEntryKinds.Manual, Label = "Phone booking", InactiveByPlan = true };
        Assert.True(RentNearBy.Core.DTOs.Owner.StayCalendarEntryDto.From(row).InactiveByPlan);
        Assert.True(RentNearBy.Core.DTOs.Owner.TaxiCalendarEntryDto.From(row).InactiveByPlan);
        Assert.False(RentNearBy.Core.DTOs.Owner.StayCalendarEntryDto.From(new CalendarEntryRow { Kind = CalendarEntryKinds.Auto, Label = "x" }).InactiveByPlan);
    }

    [Fact]
    public void Blocked_ranges_are_unchanged_every_block_keeps_blocking()
    {
        // D3: old blocks never go inactive (that would reopen dates to guests); only a new block above the limit is refused.
        Assert.DoesNotContain("InactiveByPlan", OwnerCalendarSql.ActiveBlocks);
        Assert.DoesNotContain("PlanRowInactive", Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be1", "BlockedRangeService.cs"));
    }
}
