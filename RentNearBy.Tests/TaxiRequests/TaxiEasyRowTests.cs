using System.Text.Json;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Repositories;

namespace RentNearBy.Tests.TaxiRequests;

// Contract section 4: the `taxi` Make it easy row and the trip detail planLock / taxiRequest fields from the new tables.
public class TaxiEasyRowTests
{
    private static readonly DateOnly Start = new(2026, 12, 18);
    private static readonly Guid RequestId = Guid.Parse("3f9a6c2d-1b7e-4f0a-8c55-2e7d9b4a6101");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static TripPlan Plan(int days = 3)
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start },
            Legs = [new TripCityLeg { Index = 1, CityKey = 210000001, CityName = "Nainital" }],
        };
        for (var d = 1; d <= days; d++)
        {
            var day = new TripDay { Day = d, Leg = 1 };
            day.Spots.Add(new TripSpot { Id = $"s{d}", Name = "Spot", VisitMinutes = 60 });
            plan.Days.Add(day);
        }
        return plan;
    }

    private static Trip TripOf(TripPlan plan) => new()
    {
        Id = Guid.NewGuid(), Status = TripStatuses.Planned, Title = "Uttarakhand Hills Trip", NameChosen = true, CityName = "Nainital",
        StartDate = plan.Settings.StartDate, EndDate = plan.EndDate, PlanJson = "{}",
    };

    private static TripTaxiRequestSummary Request(string state, string? view = null, TripTaxiBookingSummary? booking = null, long version = 6)
        => new(RequestId, state, view ?? TaxiRequestViewRules.Derive(state, state == RequestStates.Expired ? RequestEndReasons.NotBooked : null,
            false, state == RequestStates.Open, null, false), version, booking);

    private static TripTaxiBookingSummary Booking(string status) => new(
        Guid.Parse("9b1d4e2a-7c3f-4a6b-8d5e-1f2a3b4c5d61"), status, Start, Start.AddDays(2), "Pradeep Bisht", "Innova Crysta", 7, "Nainital");

    private static TripEnquiry LegacyTaxi(string status) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Taxi,
        Status = status,
        ListingName = "Ramesh Taxi",
        DaysJson = "[1,2,3]",
        CheckIn = Start,
        CheckOut = Start.AddDays(3),
        AdvanceStatus = TripAdvanceStatuses.None,
        UpdatedAt = new DateTime(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc),
    };

    private static TripEasyRow TaxiRowOf(TripPlan plan, TripTaxiRequestSummary? request, params TripEnquiry[] legacy)
        => TripEasyRows.Build(TripOf(plan), plan, legacy, [], null, request).Single(r => r.Key.Kind == TripRowKinds.Taxi);

    [Fact]
    public void No_request_is_todo()
    {
        var row = TaxiRowOf(Plan(), null);
        Assert.Equal(TripRowStates.Todo, row.State);
        Assert.False(row.NeedsYou);
    }

    [Theory]
    [InlineData(RequestStates.Open, TripRowStates.Asked, false)]
    [InlineData(RequestStates.Collecting, TripRowStates.Offer, true)]
    [InlineData(RequestStates.Selected, TripRowStates.Booked, false)]
    [InlineData(RequestStates.Booked, TripRowStates.Confirmed, false)]
    [InlineData(RequestStates.InProgress, TripRowStates.Confirmed, false)]
    [InlineData(RequestStates.Completed, TripRowStates.Confirmed, false)]
    [InlineData(RequestStates.Cancelled, TripRowStates.Todo, false)]
    [InlineData(RequestStates.Expired, TripRowStates.Todo, false)]
    public void Row_state_follows_the_request_state(string state, string rowState, bool needsYou)
    {
        var row = TaxiRowOf(Plan(), Request(state));
        Assert.Equal(rowState, row.State);
        Assert.Equal(needsYou, row.NeedsYou);
        var dto = TripEasyRows.ToDto(row);
        Assert.Equal("taxi", dto.Key);
        Assert.Equal(rowState, dto.State);
        Assert.Null(dto.DaysTotal);
    }

    [Fact]
    public void Every_request_state_has_a_case()
    {
        var covered = new[]
        {
            RequestStates.Open, RequestStates.Collecting, RequestStates.Selected, RequestStates.Booked, RequestStates.InProgress,
            RequestStates.Completed, RequestStates.Cancelled, RequestStates.Expired,
        };
        Assert.Equal(RequestStates.All.OrderBy(s => s), covered.OrderBy(s => s));
    }

    [Fact]
    public void A_booked_request_counts_as_done_and_the_trip_reads_ready()
    {
        var plan = Plan();
        plan.Legs![0].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
        plan.Steps = new TripSteps
        {
            GettingThere = new TripStep { Status = TripStepStatuses.Own },
            ComingBack = new TripStep { Status = TripStepStatuses.Own },
        };
        var request = Request(RequestStates.Booked, booking: Booking(BookingStates.Booked));
        Assert.True(TripEasyRows.IsRowDone(TaxiRowOf(plan, request)));
        Assert.Equal(TripBookingStates.Ready, TripBookingStates.Compute(TripOf(plan), plan, [], [], request));
        Assert.Equal(TripBookingStates.Confirming, TripBookingStates.Compute(TripOf(plan), plan, [], [], Request(RequestStates.Open)));
    }

    [Fact]
    public void An_active_request_wins_over_legacy_rows_and_a_stored_mark()
    {
        var plan = Plan();
        plan.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };
        var row = TaxiRowOf(plan, Request(RequestStates.Collecting), LegacyTaxi(TripEnquiryStatuses.Asked));
        Assert.Equal(TripRowStates.Offer, row.State);
        Assert.Equal(RequestId, row.TaxiRequest!.RequestId);
        Assert.Empty(row.Enquiries);
    }

    [Fact]
    public void Legacy_enquiries_still_drive_the_row_without_a_request()
    {
        var plan = Plan();
        Assert.Equal(TripRowStates.Asked, TaxiRowOf(plan, null, LegacyTaxi(TripEnquiryStatuses.Asked)).State);
        Assert.Equal(TripRowStates.Offer, TaxiRowOf(plan, null, LegacyTaxi(TripEnquiryStatuses.FareSent)).State);
        Assert.Equal(TripRowStates.Booked, TaxiRowOf(plan, null, LegacyTaxi(TripEnquiryStatuses.BookedByTraveller)).State);
        var confirmed = TaxiRowOf(plan, null, LegacyTaxi(TripEnquiryStatuses.ConfirmedByBoth));
        Assert.Equal(TripRowStates.Confirmed, confirmed.State);
        Assert.Equal(3, confirmed.DaysTotal);
        Assert.Equal(3, confirmed.DaysCovered);
        Assert.Null(confirmed.TaxiRequest);
    }

    [Theory]
    [InlineData(RequestStates.Cancelled)]
    [InlineData(RequestStates.Expired)]
    public void Ended_request_is_todo_over_legacy_rows_and_a_stored_mark_reads_after_it(string state)
    {
        var plan = Plan();
        Assert.Equal(TripRowStates.Todo, TaxiRowOf(plan, Request(state), LegacyTaxi(TripEnquiryStatuses.Asked)).State);
        Assert.Equal(TripRowStates.Todo, TaxiRowOf(plan, Request(state), LegacyTaxi(TripEnquiryStatuses.ConfirmedByBoth)).State);
        plan.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };
        Assert.Equal(TripRowStates.Skipped, TaxiRowOf(plan, Request(state)).State);
        plan.TaxiMark = null;
        var row = TaxiRowOf(plan, Request(state));
        Assert.Equal(TripRowStates.Todo, row.State);
        Assert.Equal(RequestId, row.TaxiRequest!.RequestId);
        Assert.Empty(row.Enquiries);
    }

    [Fact]
    public void Ended_request_stays_in_the_trip_fields()
    {
        foreach (var (state, reason, view) in new[]
                 {
                     (RequestStates.Cancelled, RequestEndReasons.TravellerCancelled, RequestViews.Ended),
                     (RequestStates.Expired, RequestEndReasons.AllDriversBusy, RequestViews.Busy),
                     (RequestStates.Expired, RequestEndReasons.NotBooked, RequestViews.Ended),
                 })
        {
            var summary = TripTaxiRequestReads.ToSummary(new TripTaxiRequestReads.SummaryRow
            {
                RequestId = RequestId, TripId = Guid.NewGuid(), Status = state, EndReason = reason, Version = 9,
            });
            var plan = Plan();
            var dto = TripHandlers.ToDto(TripOf(plan), plan, [], [], null, summary);

            Assert.False(dto.PlanLock.Locked);
            Assert.Null(dto.PlanLock.Reason);
            Assert.Null(dto.PlanLock.RequestId);
            Assert.NotNull(dto.TaxiRequest);
            Assert.Equal(RequestId, dto.TaxiRequest!.RequestId);
            Assert.Equal(state, dto.TaxiRequest.State);
            Assert.Equal(view, dto.TaxiRequest.View);
            Assert.Equal(9, dto.TaxiRequest.Version);
        }

        var completed = TripTaxiRequestReads.ToSummary(new TripTaxiRequestReads.SummaryRow
        {
            RequestId = RequestId, Status = RequestStates.Completed, Version = 12, BookingId = Guid.NewGuid(),
            BookingStatus = BookingStates.Completed, Rated = false,
        });
        Assert.Equal(RequestViews.Rate, completed.View);
        Assert.False(completed.KeepsTripOpen);
    }

    [Fact]
    public void Trip_never_had_a_request_gives_null_and_an_open_lock()
    {
        var plan = Plan();
        var dto = TripHandlers.ToDto(TripOf(plan), plan);
        Assert.Null(dto.TaxiRequest);
        Assert.False(dto.PlanLock.Locked);
        var json = JsonSerializer.Serialize(dto, Web);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("taxiRequest").ValueKind);
        Assert.Equal("""{"locked":false,"reason":null,"requestId":null}""", doc.RootElement.GetProperty("planLock").GetRawText());
    }

    [Theory]
    [InlineData(RequestStates.Open, PlanLockReasons.TaxiRequestActive)]
    [InlineData(RequestStates.Selected, PlanLockReasons.TaxiRequestActive)]
    [InlineData(RequestStates.Booked, PlanLockReasons.TaxiBookingActive)]
    [InlineData(RequestStates.InProgress, PlanLockReasons.TaxiBookingActive)]
    public void Active_request_locks_the_plan(string state, string reason)
    {
        var plan = Plan();
        var dto = TripHandlers.ToDto(TripOf(plan), plan, [], [], null, Request(state));
        Assert.True(dto.PlanLock.Locked);
        Assert.Equal(reason, dto.PlanLock.Reason);
        Assert.Equal(RequestId, dto.PlanLock.RequestId);
    }

    [Fact]
    public void Trip_fields_equal_the_contract_sample()
    {
        var summary = TripTaxiRequestReads.ToSummary(new TripTaxiRequestReads.SummaryRow
        {
            RequestId = RequestId, TripId = Guid.NewGuid(), Status = RequestStates.Collecting, Version = 6,
            HasRingingOffer = true, AnyOfferMissedOrDeclined = true,
        });
        var plan = Plan();
        var dto = TripHandlers.ToDto(TripOf(plan), plan, [], [], null, summary);
        var fields = new TripTaxiFieldsDto { PlanLock = dto.PlanLock, TaxiRequest = dto.TaxiRequest };

        var sample = File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, "trip_taxi_fields.json"));
        using var expected = JsonDocument.Parse(sample);
        using var actual = JsonDocument.Parse(JsonSerializer.Serialize(fields, Web));
        Assert.Equal(Normalize(expected.RootElement), Normalize(actual.RootElement));

        // The trip detail itself carries both properties under the same names.
        using var detail = JsonDocument.Parse(JsonSerializer.Serialize(dto, Web));
        Assert.Equal(Normalize(expected.RootElement.GetProperty("planLock")), Normalize(detail.RootElement.GetProperty("planLock")));
        Assert.Equal(Normalize(expected.RootElement.GetProperty("taxiRequest")), Normalize(detail.RootElement.GetProperty("taxiRequest")));
    }

    [Fact]
    public void Waiting_request_reads_finding_waiting()
    {
        var summary = TripTaxiRequestReads.ToSummary(new TripTaxiRequestReads.SummaryRow
        {
            RequestId = RequestId, Status = RequestStates.Open, Version = 3,
            QueueWaitStartedAt = new DateTime(2026, 12, 10, 9, 41, 7, DateTimeKind.Utc), AnyOfferMissedOrDeclined = true,
        });
        Assert.Equal(RequestViews.FindingWaiting, summary.View);
        Assert.Equal(TripRowStates.Asked, TaxiRowOf(Plan(), summary).State);
    }

    [Fact]
    public void Summary_rows_count_only_for_the_trip_owner()
    {
        var trip = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var rows = new[]
        {
            new TripTaxiRequestReads.SummaryRow { RequestId = RequestId, TripId = trip, TravellerUserId = Guid.NewGuid(), Status = RequestStates.Open },
        };
        Assert.Empty(TripTaxiRequestReads.Pick(rows, new Dictionary<Guid, Guid> { [trip] = owner }));
        rows[0].TravellerUserId = owner;
        Assert.Equal(RequestId, TripTaxiRequestReads.Pick(rows, new Dictionary<Guid, Guid> { [trip] = owner })[trip].RequestId);
    }

    [Fact]
    public void Summary_sql_prefers_the_active_request_and_reads_through_the_traveller_index()
    {
        var sql = TripTaxiRequestReads.SummarySql;
        Assert.Contains("DISTINCT ON (q.\"TripId\")", sql);
        Assert.Contains($"(q.\"Status\" IN ({RequestStates.ActiveSqlList})) DESC, q.\"CreatedAt\" DESC", sql);
        Assert.Contains("q.\"TravellerUserId\" = ANY(@owners)", sql);
        Assert.Contains("k.\"TravellerUserId\" = r.\"TravellerUserId\"", sql);
    }

    private static string Normalize(JsonElement e) => JsonSerializer.Serialize(e);
}
