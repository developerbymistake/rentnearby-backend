using RentNearBy.Api.Handlers.Owner.Be1;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Tests.Owner.Be1;
using RentNearBy.Tests.TaxiRequests;

namespace RentNearBy.Tests.TaxiAvailability;

// I-25 (shared contract 1-6): a taxi phone booking hides the taxi unless KeepVisible; it may land on a day off; every manual write
// bumps taxis:avail:gen. Stay calendar rows never carry KeepVisible.
public class TaxiCalendarMergeTests
{
    private static readonly DateOnly D = Be1Kit.Today;

    private static ManualEntryInput Taxi(bool keepVisible = false)
        => new("Airport drop", D, D, new TimeOnly(9, 0), "Haldwani", "Pantnagar", null, default, keepVisible);

    [Fact]
    public void Hiding_is_a_live_app_booking_or_a_manual_row_without_keep_visible()
    {
        var sql = TaxiAvailabilitySql.Hiding("c");
        Assert.Equal("NOT c.\"Cancelled\" AND (c.\"Kind\" = 'Auto' OR (c.\"Kind\" = 'Manual' AND NOT c.\"KeepVisible\"))", sql);
        Assert.Contains(TaxiAvailabilitySql.Hiding("ce"), TaxiAvailabilitySql.FreeOn("t", "@day"));
        Assert.Contains(TaxiAvailabilitySql.Hiding("ce"), TaxiAvailabilitySql.PublicOn("t", "@day"));
    }

    [Fact]
    public void A_day_off_save_is_refused_over_hiding_rows_and_the_state_lists_them_as_booked()
    {
        Assert.Contains(TaxiAvailabilitySql.Hiding("c"), TaxiDayOffService.FlagsSql);
        Assert.Contains(TaxiAvailabilitySql.Hiding("c"), TaxiDayOffService.StateSql);
        // Home counts stay app bookings only.
        Assert.Contains("c.\"Kind\" = 'Auto' AND NOT c.\"Cancelled\"", TaxiDayOffService.StateSql);
        Assert.Contains("AS \"MonthBookings\"", TaxiDayOffService.StateSql);
    }

    [Fact]
    public void Keep_visible_is_read_written_and_part_of_the_replay_test()
    {
        Assert.Contains("c.\"KeepVisible\"", OwnerCalendarSql.Columns());
        Assert.Contains("@keepVisible", OwnerCalendarSql.Insert);
        Assert.Contains("\"KeepVisible\" = @keepVisible", OwnerCalendarSql.Update);
        Assert.DoesNotContain("\"KeepVisible\" =", OwnerCalendarSql.Money);

        var row = new CalendarEntryRow
        {
            Id = Guid.NewGuid(), Kind = CalendarEntryKinds.Manual, ListingKind = ListingKinds.Taxi, DateFrom = D, DateTo = D,
            Label = "Airport drop", TimeOfDay = new TimeOnly(9, 0), FromPlace = "Haldwani", ToPlace = "Pantnagar",
        };
        Assert.True(OwnerCalendarRules.SameManual(row, Taxi()));
        Assert.False(OwnerCalendarRules.SameManual(row, Taxi(keepVisible: true)));
        row.KeepVisible = true;
        Assert.True(OwnerCalendarRules.SameManual(row, Taxi(keepVisible: true)));
        Assert.True(TaxiCalendarEntryDto.From(row).KeepVisible);
    }

    [Fact]
    public void Keep_visible_defaults_off_and_stays_off_for_a_stay_row()
    {
        Assert.False(new TaxiCalendarEntryRequest().KeepVisible);
        Assert.False(new ManualEntryInput("Sharma family", D, D.AddDays(1), null, null, null, null, default).KeepVisible);
        Assert.True(OwnerCalendarWriter.ForKind(ListingKinds.Taxi, Taxi(keepVisible: true)).KeepVisible);
        Assert.False(OwnerCalendarWriter.ForKind(ListingKinds.Stay, Taxi(keepVisible: true)).KeepVisible);
        var request = new TaxiCalendarEntryRequest { Label = "Airport drop", DateFrom = D, KeepVisible = true };
        Assert.True(TaxiCalendarHandlers.Input(request, null).KeepVisible);
    }

    [Fact]
    public void Taxi_manual_writes_bump_availability_and_stay_and_money_writes_do_not()
    {
        var taxi = TaxiSource.Read("RentNearBy.Api", "Handlers", "Owner", "Be1", "TaxiCalendarHandlers.cs");
        Assert.Equal(3, taxi.Split("if (saved) await BumpAvailabilityAsync(redis);").Length - 1);
        Assert.Contains("FeedCache.BumpAsync(redis, [ListingCacheKeys.TaxiAvailGen])", taxi);
        var stay = TaxiSource.Read("RentNearBy.Api", "Handlers", "Owner", "Be1", "StayCalendarHandlers.cs");
        Assert.DoesNotContain("TaxiAvailGen", stay);
        Assert.DoesNotContain("BumpAvailability", stay);
    }

    [Fact]
    public void The_writer_clears_days_off_for_hiding_taxi_rows_only_and_never_refuses_a_day_off()
    {
        var writer = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "Owner", "Be1", "OwnerCalendarWriter.cs");
        Assert.Contains("if (ctx.Kind == ListingKinds.Taxi && !inserted[0].KeepVisible) await ClearDaysOffAsync(ctx, inserted[0], c);", writer);
        Assert.Contains("if (changed && !result.Value!.Entry.KeepVisible) await ClearDaysOffAsync(ctx, result.Value!.Entry, c);", writer);
        Assert.Equal(2, writer.Split("await ClearDaysOffAsync(").Length - 1);
        Assert.Contains("if (ctx.Kind != ListingKinds.Taxi) return (await UpdateRowAsync(ctx, id, v, input, ct)).Result;", writer);
        Assert.DoesNotContain("return DayIsOff(", writer);
    }

    [Fact]
    public void Manual_hiding_is_the_manual_half_of_hiding()
    {
        Assert.Equal("c.\"Kind\" = 'Manual' AND NOT c.\"KeepVisible\" AND NOT c.\"Cancelled\"", TaxiAvailabilitySql.ManualHiding("c"));
        Assert.DoesNotContain("'Auto'", TaxiAvailabilitySql.ManualHiding("c"));
    }

    // Owner 2026-11-28: a phone booking makes the driver busy that day, so confirm and the Selected banner see it like a day off.
    [Fact]
    public void Confirm_and_the_banner_see_a_hiding_manual_row_and_accept_and_select_do_not()
    {
        var sql = RentNearBy.Infrastructure.Services.TaxiRequests.DriverCalendar.OverlapSql;
        Assert.Contains("FROM \"CalendarEntries\" c", sql);
        Assert.Contains("c.\"ListingKind\" = 'Taxi' AND c.\"ListingId\" = @driver AND " + TaxiAvailabilitySql.ManualHiding("c"), sql);
        Assert.Contains("c.\"DateFrom\" <= @to AND c.\"DateTo\" >= @from", sql);
        Assert.Equal(3, sql.Split("UNION ALL").Length - 1);
        Assert.DoesNotContain("CalendarEntries", RentNearBy.Infrastructure.Services.TaxiRequests.DriverCalendar.BookingOverlapSql);
    }
}
