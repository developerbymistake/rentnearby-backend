using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Repositories;

namespace RentNearBy.Tests.TaxiRequests;

// Blocker 16.3 item 7: auto-complete and Complete see the taxi request tables, not only TripEnquiries.
public class TripAutoCompleteTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly Guid RequestId = Guid.Parse("3f9a6c2d-1b7e-4f0a-8c55-2e7d9b4a6101");

    private static TripPlan Plan()
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start },
            Legs = [new TripCityLeg { Index = 1, CityKey = 210000001, CityName = "Nainital" }],
        };
        for (var d = 1; d <= 3; d++)
        {
            var day = new TripDay { Day = d, Leg = 1 };
            day.Spots.Add(new TripSpot { Id = $"s{d}", Name = "Spot" });
            plan.Days.Add(day);
        }
        return plan;
    }

    private static Trip PlannedTrip() => new()
    {
        Id = Guid.NewGuid(), Status = TripStatuses.Planned, StartDate = Start, EndDate = Start.AddDays(2),
    };

    private static TripTaxiBookingSummary Booking(string status)
        => new(Guid.NewGuid(), status, Start, Start.AddDays(2), "Pradeep Bisht", "Innova", 7, "Nainital");

    private static TripTaxiRequestSummary Request(string state, TripTaxiBookingSummary? booking = null)
        => new(RequestId, state, RequestViews.Finding, 4, booking);

    [Theory]
    [InlineData(RequestStates.Open)]
    [InlineData(RequestStates.Collecting)]
    [InlineData(RequestStates.Selected)]
    [InlineData(RequestStates.Booked)]
    [InlineData(RequestStates.InProgress)]
    public void Trip_with_an_active_request_is_not_auto_completed(string state)
    {
        var plan = Plan();
        Assert.True(TripBookingStates.IsOpenForCompletion(Request(state)));
        Assert.False(TripCompleteRules.AutoCompletes(PlannedTrip(), plan, [], [], Request(state)));
    }

    [Fact]
    public void An_active_booking_keeps_the_trip_open_even_when_its_request_reads_ended()
        => Assert.False(TripCompleteRules.AutoCompletes(PlannedTrip(), Plan(), [], [],
            Request(RequestStates.Cancelled, Booking(BookingStates.InProgress))));

    [Fact]
    public void A_completed_request_is_work_done_so_the_trip_auto_completes()
        => Assert.True(TripCompleteRules.AutoCompletes(PlannedTrip(), Plan(), [], [],
            Request(RequestStates.Completed, Booking(BookingStates.Completed))));

    [Theory]
    [InlineData(RequestStates.Cancelled)]
    [InlineData(RequestStates.Expired)]
    public void An_ended_request_alone_leaves_an_untouched_trip_for_the_traveller(string state)
        => Assert.False(TripCompleteRules.AutoCompletes(PlannedTrip(), Plan(), [], [], Request(state)));

    [Fact]
    public void Legacy_inputs_give_the_same_answer_as_before()
    {
        var plan = Plan();
        var open = new TripEnquiry { Id = Guid.NewGuid(), Kind = TripEnquiryKinds.Taxi, Status = TripEnquiryStatuses.Asked, DaysJson = "[1]", CheckIn = Start, CheckOut = Start.AddDays(1) };
        var done = new TripEnquiry { Id = Guid.NewGuid(), Kind = TripEnquiryKinds.Taxi, Status = TripEnquiryStatuses.ConfirmedByBoth, DaysJson = "[1,2,3]", CheckIn = Start, CheckOut = Start.AddDays(3) };
        Assert.False(TripCompleteRules.AutoCompletes(PlannedTrip(), plan, [open], [], null));
        Assert.True(TripCompleteRules.AutoCompletes(PlannedTrip(), plan, [done], [], null));
        Assert.False(TripCompleteRules.AutoCompletes(PlannedTrip(), plan, [], [], null));
        Assert.False(TripBookingStates.IsOpenForCompletion((TripTaxiRequestSummary?)null));
    }

    [Theory]
    [InlineData(RequestStates.Open, PlanLockReasons.TaxiRequestActive, TripCompleteRules.TaxiRequestOpenMessage)]
    [InlineData(RequestStates.Selected, PlanLockReasons.TaxiRequestActive, TripCompleteRules.TaxiRequestOpenMessage)]
    [InlineData(RequestStates.Booked, PlanLockReasons.TaxiBookingActive, TripCompleteRules.TaxiBookingOpenMessage)]
    [InlineData(RequestStates.InProgress, PlanLockReasons.TaxiBookingActive, TripCompleteRules.TaxiBookingOpenMessage)]
    public void Manual_complete_is_refused_while_the_request_is_active(string state, string reason, string message)
    {
        var refusal = TripCompleteRules.TaxiRefusal(Request(state));
        Assert.NotNull(refusal);
        Assert.Equal(409, refusal!.StatusCode);
        Assert.Equal(TaxiRequestErrorTypes.PlanLocked, refusal.ErrorType);
        Assert.Equal(message, refusal.Message);
        Assert.Equal(reason, refusal.Data!.Reason);
        Assert.Equal(RequestId, refusal.Data.RequestId);
    }

    [Theory]
    [InlineData(RequestStates.Completed)]
    [InlineData(RequestStates.Cancelled)]
    [InlineData(RequestStates.Expired)]
    public void Manual_complete_is_not_refused_for_an_ended_request(string state)
    {
        Assert.Null(TripCompleteRules.TaxiRefusal(Request(state)));
        Assert.Null(TripCompleteRules.TaxiRefusal(null));
    }

    [Fact]
    public void Candidate_sql_excludes_trips_with_an_active_request_or_booking()
    {
        var sql = TripRepository.AutoCompleteCandidatesSql;
        Assert.Contains($"r.\"TripId\" = t.\"Id\" AND r.\"Status\" IN ({RequestStates.ActiveSqlList})", sql);
        Assert.Contains("b.\"TravellerUserId\" = t.\"OwnerUserId\" AND b.\"TripId\" = t.\"Id\"", sql);
        Assert.Contains($"b.\"Status\" IN ({BookingStates.ActiveSqlList})", sql);
        // Open enquiries keep the trip open; a confirmed booking never does (no advance step).
        Assert.Contains("e.\"Status\" IN ('Asked', 'Offered', 'Countered', 'FareSent')", sql);
        Assert.DoesNotContain("Advance", sql);
    }

    // Trip compaction: an archived trip's plan is "{}", so the auto-complete pass never picks it up.
    [Fact]
    public void Candidate_sql_skips_archived_trips()
        => Assert.Contains("t.\"ArchivedAt\" IS NULL", TripRepository.AutoCompleteCandidatesSql);
}
