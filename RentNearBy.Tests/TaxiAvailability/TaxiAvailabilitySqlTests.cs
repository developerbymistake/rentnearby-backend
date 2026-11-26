using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Tests.TaxiRequests;
using TaxiAvail = RentNearBy.Infrastructure.Services.TaxiAvailability;

namespace RentNearBy.Tests.TaxiAvailability;

// Contract 1.9: one building block, used inside every list statement; parameters only.
public class TaxiAvailabilitySqlTests
{
    private static ApplicationDbContext DesignContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite()).Options);

    [Fact]
    public void Free_on_holds_both_not_exists_with_the_day_as_a_parameter()
    {
        var sql = TaxiAvailabilitySql.FreeOn("t", "@day");
        Assert.Contains("NOT EXISTS (SELECT 1 FROM \"TaxiDayOffs\" dof WHERE dof.\"TaxiListingId\" = t.\"Id\" AND dof.\"Date\" = @day)", sql);
        Assert.Contains("FROM \"CalendarEntries\" ce WHERE ce.\"ListingKind\" = 'Taxi' AND ce.\"ListingId\" = t.\"Id\" AND ce.\"Kind\" = 'Auto' AND NOT ce.\"Cancelled\"", sql);
        Assert.Contains("ce.\"DateTo\" >= @day AND ce.\"DateFrom\" <= @day", sql);
        Assert.True(TaxiAvailabilityRules.BookingHides);
    }

    [Fact]
    public void Booking_hides_false_drops_only_the_booking_part()
    {
        var sql = TaxiAvailabilitySql.FreeOn("t", "@day", bookingHides: false);
        Assert.Contains("\"TaxiDayOffs\"", sql);
        Assert.DoesNotContain("\"CalendarEntries\"", sql);
    }

    [Fact]
    public void Public_on_adds_the_switch_status_and_deleted_rule()
    {
        var sql = TaxiAvailabilitySql.PublicOn("t", "@day");
        Assert.StartsWith("t.\"Status\" = 'Approved' AND t.\"IsAvailable\" AND NOT t.\"IsDeleted\" AND NOT EXISTS", sql);
    }

    [Fact]
    public void Linq_twin_names_the_same_tables_and_predicates()
    {
        using var db = DesignContext();
        var day = new DateOnly(2026, 11, 27);
        var sql = db.TaxiListings.Where(TaxiAvail.FreeOn(db, day)).Select(t => t.Id).ToQueryString();
        Assert.Contains("\"TaxiDayOffs\"", sql);
        Assert.Contains("\"CalendarEntries\"", sql);
        Assert.Contains("\"Cancelled\"", sql);
        Assert.Contains("'Auto'", sql);
        Assert.Contains("'Taxi'", sql);
        var noBooking = db.TaxiListings.Where(TaxiAvail.FreeOn(db, day, bookingHides: false)).Select(t => t.Id).ToQueryString();
        Assert.DoesNotContain("\"CalendarEntries\"", noBooking);
    }

    [Fact]
    public void Nearby_page_sql_applies_the_rule_only_for_taxi_with_a_day()
    {
        var day = new DateOnly(2026, 11, 27);
        var taxi = NearbyFeedQuery.BuildPageSql(NearbyKind.Taxi, false, new NearbyFilter(Day: day));
        Assert.Contains(TaxiAvailabilitySql.FreeOn("t", "@day"), taxi);
        Assert.DoesNotContain("2026", taxi);
        Assert.DoesNotContain("TaxiDayOffs", NearbyFeedQuery.BuildPageSql(NearbyKind.Taxi, false, new NearbyFilter()));
        Assert.DoesNotContain("TaxiDayOffs", NearbyFeedQuery.BuildPageSql(NearbyKind.Stay, false, new NearbyFilter(Day: day)));
        Assert.DoesNotContain("TaxiDayOffs", NearbyFeedQuery.BuildPageSql(NearbyKind.StayJob, false, new NearbyFilter(Day: day)));
    }

    [Fact]
    public void Route_search_sql_carries_the_rule_with_a_day_and_orders_by_fare_when_cheapest()
    {
        var dated = TaxiListingRepository.RouteSearchSql(withDay: true, cheapest: false);
        Assert.Contains(TaxiAvailabilitySql.FreeOn("t", "@day"), dated);
        Assert.Contains("ORDER BY t.\"PriorityRank\" DESC, b.\"Reversed\", b.\"Fare\", t.\"Id\"", dated);
        Assert.DoesNotContain("TaxiDayOffs", TaxiListingRepository.RouteSearchSql(withDay: false, cheapest: false));
        Assert.Contains("ORDER BY b.\"Fare\", b.\"Reversed\", t.\"PriorityRank\" DESC, t.\"Id\"", TaxiListingRepository.RouteSearchSql(true, true));
    }

    [Fact]
    public void Free_count_is_one_statement_over_the_whole_match_plus_passing_ids()
    {
        var sql = TaxiListingRepository.RouteFreeCountSql(withDay: true);
        Assert.Contains("count(*)::int AS \"Value\"", sql);
        Assert.Contains("UNION", sql);
        Assert.Contains("unnest(@extra)", sql);
        Assert.Contains(TaxiAvailabilitySql.FreeOn("t", "@day"), sql);
        Assert.DoesNotContain("LIMIT", sql);
    }

    [Fact]
    public void Passing_filter_and_popular_aggregate_use_the_public_rule()
    {
        Assert.Contains(TaxiAvailabilitySql.PublicOn("t", "@day"), TaxiFeedHandlers.BusySql);
        Assert.Contains("= ANY(@ids)", TaxiFeedHandlers.BusySql);
        Assert.Contains(TaxiAvailabilitySql.PublicOn("t", "@day"), PopularRouteReader.Sql);
        Assert.Contains("CROSS JOIN LATERAL", PopularRouteReader.Sql);
        Assert.Contains("ORDER BY p.\"SortOrder\", p.\"Id\"", PopularRouteReader.Sql);
        Assert.Contains("WHERE p.\"IsActive\" AND a.\"Taxis\" > 0", PopularRouteReader.Sql);
    }

    [Fact]
    public void Calendar_create_and_update_know_the_day_off_and_confirm_sees_it_as_a_clash()
    {
        Assert.Contains("\"TaxiDayOffs\"", OwnerCalendarSql.CreateCheck);
        Assert.Contains("AS \"DayOff\"", OwnerCalendarSql.CreateCheck);
        Assert.Contains("(c.\"ListingKind\" <> 'Taxi' OR c.\"DateFrom\" = @from OR NOT EXISTS (SELECT 1 FROM \"TaxiDayOffs\"", OwnerCalendarSql.Update);
        Assert.Contains("FROM \"TaxiDayOffs\" d", DriverCalendar.OverlapSql);
        Assert.Contains("d.\"TaxiListingId\" = @driver AND d.\"Date\" BETWEEN @from AND @to", DriverCalendar.OverlapSql);
        Assert.Contains("ORDER BY x.\"From\", x.\"To\"", DriverCalendar.OverlapSql);
        Assert.EndsWith("LIMIT 1", DriverCalendar.OverlapSql.Trim());
        // Q4: delete and money are never refused by a day off.
        Assert.DoesNotContain("TaxiDayOffs", OwnerCalendarSql.Delete);
        Assert.DoesNotContain("TaxiDayOffs", OwnerCalendarSql.Money);
    }

    // Contract 1.8 / R16: only confirm (and its Selected banner) refuses a day off; accept and select read bookings only.
    [Fact]
    public void Accept_and_select_read_bookings_only_and_confirm_and_the_banner_read_the_day_off_too()
    {
        Assert.DoesNotContain("TaxiDayOffs", DriverCalendar.BookingOverlapSql);
        Assert.Contains("FROM \"TaxiBookings\" b", DriverCalendar.BookingOverlapSql);
        Assert.Contains("FROM \"TripEnquiries\" e", DriverCalendar.BookingOverlapSql);
        Assert.Contains("(@exceptEnquiry IS NULL OR e.\"Id\" <> @exceptEnquiry)", DriverCalendar.BookingOverlapSql);
        Assert.EndsWith("LIMIT 1", DriverCalendar.BookingOverlapSql.Trim());

        var calendar = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "DriverCalendar.cs");
        var conflict = TaxiSource.Member(calendar, "public static Task<TaxiDateRange?> ConflictForAsync(");
        Assert.Contains("FindBookingOverlapAsync(", conflict);
        var selected = TaxiSource.Member(calendar, "public static Task<TaxiDateRange?> SelectedConflictForAsync(");
        Assert.Contains("FindOverlapAsync(", selected);
        Assert.DoesNotContain("FindBookingOverlapAsync(", selected);
        var bookingFind = TaxiSource.Member(calendar, "public async Task<TaxiDateRange?> FindBookingOverlapAsync(");
        Assert.Contains("SqlQueryRaw<RangeRow>(DriverCalendar.BookingOverlapSql", bookingFind);

        var views = TaxiSource.Read("RentNearBy.Api", "Handlers", "TaxiRequests", "TaxiDriverViews.cs");
        Assert.Contains("DriverCalendar.SelectedConflictForAsync(db, o.DriverListingId, r.DateFrom, r.DateTo, ct)", views);
        Assert.DoesNotContain("DriverCalendar.ConflictForAsync(", views);
    }

    [Fact]
    public void Day_off_state_and_flags_are_single_statements_on_the_taxi()
    {
        Assert.Contains("FROM \"TaxiListings\" l WHERE l.\"Id\" = @l", TaxiDayOffService.StateSql);
        Assert.Contains("unnest(@setDates)", TaxiDayOffService.FlagsSql);
        Assert.Contains("AS \"Booked\"", TaxiDayOffService.FlagsSql);
        Assert.Contains("AS \"Off\"", TaxiDayOffService.FlagsSql);
        Assert.Contains("ON CONFLICT (\"TaxiListingId\", \"Date\") DO UPDATE", TaxiDayOffService.UpsertSql);
        Assert.Contains("= ANY(@clear)", TaxiDayOffService.ClearSql);
    }

    [Fact]
    public void Home_work_sql_counts_this_month_bookings_and_days_off_inline()
    {
        var sql = RentNearBy.Infrastructure.Services.Owner.Be4.TaxiDriverHomeReader.WorkSql;
        Assert.Contains("AS \"MonthBookings\"", sql);
        Assert.Contains("ce.\"DateFrom\" >= @m1 AND ce.\"DateFrom\" < @m2", sql);
        Assert.Contains("AS \"MonthDaysOff\"", sql);
        Assert.Contains("dof.\"Date\" >= @today AND dof.\"Date\" < @m2", sql);
    }
}
