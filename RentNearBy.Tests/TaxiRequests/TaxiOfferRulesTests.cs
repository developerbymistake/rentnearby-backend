using System.Text.Json;
using System.Text.Json.Nodes;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiOfferRulesTests
{
    private static TaxiDateRange R(int fromDay, int toDay) => new(new DateOnly(2026, 12, fromDay), new DateOnly(2026, 12, toDay));

    [Theory]
    [InlineData(18, 21, 21, 24)]
    [InlineData(21, 24, 18, 21)]
    [InlineData(18, 21, 18, 18)]
    [InlineData(18, 21, 19, 20)]
    [InlineData(19, 20, 18, 21)]
    [InlineData(18, 18, 18, 18)]
    public void Overlap_rule_is_inclusive_on_both_ends(int aFrom, int aTo, int bFrom, int bTo)
    {
        Assert.True(DriverCalendar.Overlaps(R(aFrom, aTo), R(bFrom, bTo)));
        Assert.True(DriverCalendar.Overlaps(R(bFrom, bTo), R(aFrom, aTo)));
    }

    [Theory]
    [InlineData(18, 21, 22, 24)]
    [InlineData(22, 24, 18, 21)]
    [InlineData(18, 18, 19, 19)]
    public void Back_to_back_dates_do_not_overlap(int aFrom, int aTo, int bFrom, int bTo)
    {
        Assert.False(DriverCalendar.Overlaps(R(aFrom, aTo), R(bFrom, bTo)));
        Assert.False(DriverCalendar.Overlaps(R(bFrom, bTo), R(aFrom, aTo)));
    }

    [Fact]
    public void Overlap_sql_uses_the_same_inclusive_rule_for_bookings_and_legacy_rows()
    {
        var sql = DriverCalendar.OverlapSql;
        Assert.Contains("b.\"DateFrom\" <= @to AND b.\"DateTo\" >= @from", sql);
        Assert.Contains("e.\"CheckIn\" <= @to AND COALESCE(e.\"CheckOut\", e.\"CheckIn\" + 1) > @from", sql);
        Assert.Contains("b.\"Status\" IN ('Booked', 'InProgress')", sql);
        Assert.Contains("e.\"Status\" = 'ConfirmedByBoth'", sql);
        Assert.Contains("e.\"Kind\" = 'Taxi' AND e.\"TripId\" IS NOT NULL AND e.\"Day\" IS NULL", sql);
    }

    [Fact]
    public void Overlapping_live_offers_sql_finds_only_other_requests_on_overlapping_dates_in_lock_order()
    {
        var sql = TaxiBookingService.OverlappingLiveOffersSql;
        Assert.Contains("o.\"DriverListingId\" = @driver AND o.\"RequestId\" <> @request", sql);
        Assert.Contains("o.\"Status\" IN ('Accepted', 'Selected')", sql);
        Assert.Contains("r.\"DateFrom\" <= @to AND r.\"DateTo\" >= @from", sql);
        Assert.EndsWith("ORDER BY o.\"RequestId\", o.\"Id\"", sql.Trim());
    }

    [Fact]
    public void Legacy_local_row_covers_check_in_to_the_day_before_check_out()
    {
        Assert.Equal(R(18, 21), DriverCalendar.LegacyRange(new DateOnly(2026, 12, 18), new DateOnly(2026, 12, 22)));
        Assert.Equal(R(18, 18), DriverCalendar.LegacyRange(new DateOnly(2026, 12, 18), null));
        Assert.Equal(R(18, 18), DriverCalendar.LegacyRange(new DateOnly(2026, 12, 18), new DateOnly(2026, 12, 18)));
        Assert.Null(DriverCalendar.LegacyRange(null, new DateOnly(2026, 12, 22)));
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(5, 1, 5.0)]
    [InlineData(9, 2, 4.5)]
    [InlineData(346, 72, 4.8)]
    [InlineData(14, 3, 4.7)]
    public void Rating_average_is_sum_over_count(int sum, int count, double? expected)
        => Assert.Equal(expected, TaxiBookingService.RatingAverage(sum, count));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void Stars_are_one_to_five(int stars, bool valid) => Assert.Equal(valid, TaxiBookingService.IsValidStars(stars));

    [Theory]
    [InlineData(OfferStates.Accepted, true)]
    [InlineData(OfferStates.Selected, true)]
    [InlineData(OfferStates.Confirmed, true)]
    [InlineData(OfferStates.Missed, false)]
    [InlineData(OfferStates.Declined, false)]
    public void A_repeated_accept_after_success_is_answered_ok(string state, bool ok) => Assert.Equal(ok, TaxiOfferService.IsAcceptAnswered(state));

    [Theory]
    [InlineData(OfferStates.Missed, TaxiRequestErrorTypes.OfferMissed)]
    [InlineData(OfferStates.Closed, TaxiRequestErrorTypes.OfferMissed)]
    [InlineData(OfferStates.Declined, TaxiRequestErrorTypes.OfferStateInvalid)]
    [InlineData(OfferStates.Withdrawn, TaxiRequestErrorTypes.OfferStateInvalid)]
    public void Accept_after_the_ring_ended_is_refused(string state, string error) => Assert.Equal(error, TaxiOfferService.AcceptRefusal(state));

    [Fact]
    public void Accept_counts_only_below_the_target_and_only_while_collecting()
    {
        var sql = TaxiOfferService.AcceptCountSql;
        Assert.Contains("\"AcceptedCount\" < \"AcceptTarget\"", sql);
        Assert.Contains("\"Status\" IN ('Open', 'Collecting', 'Selected')", sql);
        Assert.Contains("WHEN \"Status\" = 'Open' THEN 'Collecting'", sql);
        Assert.Contains("\"Version\" = \"Version\" + 1", sql);
    }

    [Fact]
    public void Accept_moves_only_a_ringing_offer_before_its_expiry()
    {
        var sql = TaxiOfferService.AcceptOfferSql;
        Assert.Contains("\"Status\" = 'Ringing' AND \"ExpiresAt\" > now()", sql);
        Assert.Contains("SET \"Status\" = 'Accepted'", sql);
        Assert.True(TaxiRequestTransitions.CanMove(TransitionKinds.Offer, OfferStates.Ringing, OfferStates.Accepted));
    }

    [Fact]
    public void Rating_insert_is_once_per_booking()
    {
        Assert.Contains("ON CONFLICT (\"BookingId\") DO NOTHING", TaxiBookingService.InsertRatingSql);
        Assert.DoesNotContain("Comment", TaxiBookingService.InsertRatingSql);
    }

    [Fact]
    public void Dates_already_booked_data_equals_the_contract_sample()
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, "error_dates_already_booked.json")))!;
        var data = JsonSerializer.SerializeToNode(TaxiBookingService.DatesData(R(20, 24)));
        Assert.True(JsonNode.DeepEquals(sample["data"], data));
        Assert.Equal(sample["error"]!["message"]!.GetValue<string>(),
            TaxiRequestErrorTypes.DatesAlreadyBookedMessage(new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 24)));
    }
}
