using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.Trips;

// Issue 16 v7 (trip parts, Complete my plan), Decision 14 stay half and issue 10 (one open enquiry per trip and stay).
public class TripPartsTests
{
    private static readonly DateOnly Start = new(2026, 12, 1);

    private static TripPlan Plan(int cities = 1)
    {
        var plan = new TripPlan { Settings = new TripSettings { StartDate = Start }, Legs = [] };
        for (var k = 1; k <= cities; k++)
        {
            plan.Legs.Add(new TripCityLeg { Index = k, CityKey = k, CityName = k == 1 ? "Nainital" : "Almora" });
            var day = new TripDay { Day = k, Leg = k };
            day.Spots.Add(new TripSpot { Id = $"s{k}", Name = "Spot", VisitMinutes = 60 });
            plan.Days.Add(day);
        }
        return plan;
    }

    private static Trip TripOf(TripPlan plan) => new()
    {
        Id = Guid.NewGuid(), Status = TripStatuses.Planned, StartDate = plan.Settings.StartDate, EndDate = plan.EndDate,
    };

    private static TripEnquiry Stay(string status, int night = 1, Guid? listing = null) => new()
    {
        Id = Guid.NewGuid(), TripId = Guid.NewGuid(), Kind = TripEnquiryKinds.Stay, Status = status, ListingId = listing ?? Guid.NewGuid(),
        Day = (short)night, CheckIn = Start.AddDays(night - 1), CheckOut = Start.AddDays(night),
        ContextJson = "{\"city\":\"Nainital\"}", UpdatedAt = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc),
    };

    private static TripTaxiRequestSummary Request(string state) => new(Guid.NewGuid(), state, "view", 1);

    private static List<TripEasyRow> Rows(TripPlan plan, IReadOnlyList<TripEnquiry>? enquiries = null, TripTaxiRequestSummary? request = null)
        => TripEasyRows.Build(TripOf(plan), plan, enquiries ?? [], [], null, request);

    private static TripRowMark Skipped => new() { Status = TripRowMarkStatuses.Skipped };

    [Fact]
    public void Build_has_only_stay_rows_then_taxi_and_BuildAll_keeps_the_travel_rows()
    {
        var plan = Plan(2);
        Assert.Equal(["stay:1", "stay:2", "taxi"], Rows(plan).Select(r => r.Key.Id));
        Assert.Equal(["gettingThere", "stay:1", "between:2", "stay:2", "taxi", "comingBack"],
            TripEasyRows.BuildAll(TripOf(plan), plan, [], []).Select(r => r.Key.Id));
    }

    [Fact]
    public void Saved_trip_with_nothing_else_is_33_and_next_is_stay()
    {
        var plan = Plan();
        var p = TripEasyRows.Progress(1, Rows(plan));
        Assert.Equal((3, 1, 33), (p.Total, p.Done, p.Percent));
        Assert.Equal("stay:1", p.Next!.Key);
        Assert.Equal(0, TripEasyRows.Progress(0, Rows(plan)).Percent);
    }

    [Fact]
    public void Asked_stay_counts_and_next_moves_to_taxi()
    {
        var plan = Plan();
        var p = TripEasyRows.Progress(1, Rows(plan, [Stay(TripEnquiryStatuses.Asked)]));
        Assert.Equal(67, p.Percent);
        Assert.Equal("taxi", p.Next!.Key);
    }

    [Fact]
    public void Skipped_parts_count_as_handled()
    {
        var plan = Plan();
        plan.TaxiMark = Skipped;
        Assert.Equal(67, TripEasyRows.Progress(1, Rows(plan)).Percent);
        plan.Legs![0].StayMark = Skipped;
        var p = TripEasyRows.Progress(1, Rows(plan));
        Assert.Equal((3, 100), (p.Done, p.Percent));
        Assert.Null(p.Next);
    }

    [Fact]
    public void Stay_part_is_arranged_by_any_city_and_skipped_only_when_every_city_is()
    {
        var plan = Plan(2);
        Assert.Equal(TripPartStates.Arranged, TripParts.Of(2, Rows(plan, [Stay(TripEnquiryStatuses.Asked)])).Stay);
        plan.Legs![0].StayMark = Skipped;
        Assert.Equal(TripPartStates.Open, TripParts.Of(2, Rows(plan)).Stay);
        plan.Legs[1].StayMark = Skipped;
        Assert.Equal(TripPartStates.Skipped, TripParts.Of(2, Rows(plan)).Stay);
    }

    [Fact]
    public void Percent_rounds_to_0_33_67_100()
    {
        Assert.Equal(0, new TripParts("open", "open", "open").Percent);
        Assert.Equal(33, new TripParts("arranged", "open", "open").Percent);
        Assert.Equal(67, new TripParts("arranged", "skipped", "open").Percent);
        Assert.True(new TripParts("arranged", "skipped", "arranged").Complete);
    }

    [Fact]
    public void Taxi_mark_reads_after_an_ended_request_and_a_live_request_wins_over_the_mark()
    {
        var plan = Plan();
        plan.TaxiMark = Skipped;
        Assert.Equal(TripRowStates.Skipped, Rows(plan, null, Request(RequestStates.Cancelled)).Single(r => r.Key.Kind == "taxi").State);
        Assert.Equal(TripRowStates.Asked, Rows(plan, null, Request(RequestStates.Open)).Single(r => r.Key.Kind == "taxi").State);
        plan.TaxiMark = null;
        Assert.Equal(TripRowStates.Todo, Rows(plan, null, Request(RequestStates.Expired)).Single(r => r.Key.Kind == "taxi").State);
    }

    [Fact]
    public void Complete_plan_marks_both_open_parts_and_is_idempotent()
    {
        var plan = Plan();
        Assert.True(TripCompletePlan.Apply(plan, Rows(plan), 1));
        Assert.Equal(TripRowMarkStatuses.Skipped, plan.TaxiMark!.Status);
        Assert.Equal(TripRowMarkStatuses.Skipped, plan.Legs![0].StayMark!.Status);
        Assert.Equal(100, TripEasyRows.Progress(1, Rows(plan)).Percent);
        Assert.False(TripCompletePlan.Apply(plan, Rows(plan), 1));
    }

    [Fact]
    public void Complete_plan_leaves_arranged_parts_alone()
    {
        var plan = Plan(2);
        var rows = Rows(plan, [Stay(TripEnquiryStatuses.Asked)], Request(RequestStates.Collecting));
        Assert.False(TripCompletePlan.Apply(plan, rows, 2));
        Assert.Null(plan.TaxiMark);
        Assert.All(plan.Legs!, l => Assert.Null(l.StayMark));
    }

    [Fact]
    public void Complete_plan_marks_only_stay_when_taxi_is_requested()
    {
        var plan = Plan();
        Assert.True(TripCompletePlan.Apply(plan, Rows(plan, null, Request(RequestStates.Open)), 1));
        Assert.Null(plan.TaxiMark);
        Assert.Equal(TripRowMarkStatuses.Skipped, plan.Legs![0].StayMark!.Status);
    }

    [Fact]
    public void Booked_stay_locks_the_plan_in_the_dto_and_the_taxi_lock_wins()
    {
        var booked = Stay(TripEnquiryStatuses.ConfirmedByBoth, 2);
        var earlier = Stay(TripEnquiryStatuses.BookedByTraveller, 1);
        var today = Start.AddDays(-10);
        var dto = TripTaxiRequestFields.PlanLock(null, [Stay(TripEnquiryStatuses.Asked), booked, earlier], today);
        Assert.Equal((true, PlanLockReasons.StayBookingActive, earlier.Id), (dto.Locked, dto.Reason, dto.EnquiryId));
        Assert.Null(dto.RequestId);
        Assert.False(TripTaxiRequestFields.PlanLock(null, [Stay(TripEnquiryStatuses.Asked)], today).Locked);
        Assert.Equal(PlanLockReasons.TaxiRequestActive, TripTaxiRequestFields.PlanLock(Request(RequestStates.Open), [booked], today).Reason);
    }

    // Audit BREAK: a booked stay whose check-out has come can no longer be cancelled ("This stay has already ended."), so it
    // must not lock the plan or refuse a delete for ever. The lock follows the cancel rule (ended once CheckOut <= today IST).
    [Fact]
    public void Ended_booked_stay_does_not_lock_the_plan()
    {
        var night1 = Stay(TripEnquiryStatuses.ConfirmedByBoth, 1);   // CheckOut = Start + 1
        var night2 = Stay(TripEnquiryStatuses.BookedByTraveller, 2); // CheckOut = Start + 2

        Assert.Equal(night1.Id, TripTaxiRequestFields.PlanLock(null, [night1, night2], Start).EnquiryId);
        Assert.Equal(night2.Id, TripTaxiRequestFields.PlanLock(null, [night1, night2], Start.AddDays(1)).EnquiryId);
        Assert.False(TripTaxiRequestFields.PlanLock(null, [night1, night2], Start.AddDays(2)).Locked);
        Assert.False(TripTaxiRequestFields.PlanLock(null, [night1, night2], Start.AddDays(30)).Locked);

        var noCheckOut = Stay(TripEnquiryStatuses.ConfirmedByBoth, 1);
        noCheckOut.CheckOut = null;
        Assert.True(TripTaxiRequestFields.PlanLock(null, [noCheckOut], Start.AddDays(30)).Locked);
    }

    [Fact]
    public void Stay_lock_ends_on_the_same_day_the_cancel_rule_calls_the_stay_ended()
    {
        var e = Stay(TripEnquiryStatuses.ConfirmedByBoth, 1);
        var checkOut = e.CheckOut!.Value;
        Assert.True(TripTaxiRequestFields.StayLockHolds(e, checkOut.AddDays(-1)));
        Assert.False(TripTaxiRequestFields.StayLockHolds(e, checkOut));
        Assert.False(TripTaxiRequestFields.StayLockHolds(Stay(TripEnquiryStatuses.Asked, 1), Start.AddDays(-5)));
        var taxi = Stay(TripEnquiryStatuses.ConfirmedByBoth, 1);
        taxi.Kind = TripEnquiryKinds.Taxi;
        Assert.False(TripTaxiRequestFields.StayLockHolds(taxi, Start.AddDays(-5)));
        // The cancel handler's ended test, word for word: CheckOut <= today.
        var cancelSource = File.ReadAllText(Path.Combine(BackendRoot(), "RentNearBy.Api", "Handlers", "TripEnquiryHandlers.cs"));
        Assert.Contains("if (e.CheckOut is { } checkOut && checkOut <= TripExecutionRules.TodayIst(now))", cancelSource);
    }

    private static string BackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.Api", "RentNearBy.Api.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Backend root not found");
    }

    [Fact]
    public void Stay_lock_refuses_structure_but_not_the_taxi_mark()
    {
        var stored = Plan();
        var skipTaxi = Plan();
        skipTaxi.TaxiMark = Skipped;
        var enquiryId = Guid.NewGuid();
        Assert.Null(TripPlanLock.UpdateRefusal(PlanLockReasons.StayBookingActive, null, stored, skipTaxi, enquiryId));
        Assert.NotNull(TripPlanLock.UpdateRefusal(PlanLockReasons.TaxiRequestActive, Guid.NewGuid(), stored, skipTaxi));

        var moreDays = Plan(2);
        var refusal = TripPlanLock.UpdateRefusal(PlanLockReasons.StayBookingActive, null, stored, moreDays, enquiryId)!;
        Assert.Equal("Plan is locked while your stay booking is active. Cancel the stay booking to edit.", refusal.Message);
        Assert.Equal((PlanLockReasons.StayBookingActive, (Guid?)null, (Guid?)enquiryId), (refusal.Data!.Reason, refusal.Data.RequestId, refusal.Data.EnquiryId));
        Assert.Equal("Cancel your stay booking first.", TripPlanLock.DeleteRefusal(PlanLockReasons.StayBookingActive, null, enquiryId)!.Message);
        Assert.Equal("Cancel the taxi request first.", TripPlanLock.DeleteRefusal(PlanLockReasons.TaxiBookingActive, Guid.NewGuid())!.Message);
    }

    [Fact]
    public void Booked_stay_sql_uses_the_booked_statuses()
    {
        var sql = RentNearBy.Infrastructure.Services.TaxiRequests.TripPlanLockReader.BookedStaySql;
        Assert.Contains("'ConfirmedByBoth', 'BookedByTraveller'", sql);
        Assert.Contains("\"Kind\" = 'Stay'", sql);
        Assert.Contains("AND (\"CheckOut\" IS NULL OR \"CheckOut\" > @today)", sql);
    }

    private static TripEnquiryHandlers.StaySnapshot Snapshot(Guid id)
        => new(id, Guid.NewGuid(), "Deodar", "Approved", true, false, null, null, null, "Nainital", "Nainital", null);

    [Fact]
    public void Second_send_for_the_same_listing_and_nights_returns_the_open_row()
    {
        var listing = Guid.NewGuid();
        var open = Stay(TripEnquiryStatuses.Asked, 1, listing);
        var items = new List<TripEnquiry>();
        var skipped = new List<TripSkippedDto>();
        var (_, news) = TripEnquiryHandlers.RecheckUnderLock([open], [], [Snapshot(listing)], Start, Start.AddDays(1), items, skipped);
        Assert.Empty(news);
        Assert.Equal([open], items);
    }

    [Fact]
    public void Booked_row_of_the_same_listing_for_other_nights_refuses_a_second_enquiry()
    {
        var listing = Guid.NewGuid();
        var booked = Stay(TripEnquiryStatuses.ConfirmedByBoth, 1, listing);
        var items = new List<TripEnquiry>();
        var skipped = new List<TripSkippedDto>();
        var (_, news) = TripEnquiryHandlers.RecheckUnderLock([booked], [], [Snapshot(listing)], Start.AddDays(4), Start.AddDays(5), items, skipped);
        Assert.Empty(news);
        Assert.Equal(TripSkipReasons.AlreadyOpen, skipped.Single().Reason);
    }

    [Fact]
    public void Asked_row_for_other_nights_is_still_open_for_other_nights()
    {
        var listing = Guid.NewGuid();
        var asked = Stay(TripEnquiryStatuses.Asked, 1, listing);
        var skipped = new List<TripSkippedDto>();
        TripEnquiryHandlers.RecheckUnderLock([asked], [], [Snapshot(listing)], Start.AddDays(4), Start.AddDays(5), [], skipped);
        Assert.Equal(TripSkipReasons.OpenForOtherNights, skipped.Single().Reason);
    }
}
