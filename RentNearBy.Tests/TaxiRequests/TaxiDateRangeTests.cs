using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiDateRangeTests
{
    private static readonly DateOnly Start = new(2026, 12, 18);
    private static DateOnly D(int day) => Start.AddDays(day - 1);

    [Fact]
    public void All_sections_cover_the_whole_trip_with_sightseeing_inside()
        => Assert.Equal(new TaxiDateRange(D(1), D(4)), TaxiDateRange.Occupied(true, true, true, Start, 4, [1, 2, 4]));

    [Fact]
    public void Pickup_only_occupies_the_first_day_only()
        => Assert.Equal(new TaxiDateRange(D(1), D(1)), TaxiDateRange.Occupied(true, false, false, Start, 9, []));

    [Fact]
    public void Drop_only_occupies_the_last_day_only()
        => Assert.Equal(new TaxiDateRange(D(9), D(9)), TaxiDateRange.Occupied(false, false, true, Start, 9, []));

    [Fact]
    public void Sightseeing_only_runs_from_its_first_to_its_last_chosen_day()
        => Assert.Equal(new TaxiDateRange(D(2), D(5)), TaxiDateRange.Occupied(false, true, false, Start, 9, [5, 2, 3]));

    [Fact]
    public void Pickup_and_sightseeing_run_from_day_one_to_the_last_chosen_day()
        => Assert.Equal(new TaxiDateRange(D(1), D(3)), TaxiDateRange.Occupied(true, true, false, Start, 9, [2, 3]));

    [Fact]
    public void Sightseeing_and_drop_run_from_the_first_chosen_day_to_the_last_day()
        => Assert.Equal(new TaxiDateRange(D(4), D(9)), TaxiDateRange.Occupied(false, true, true, Start, 9, [4, 6]));

    [Fact]
    public void Sections_off_have_no_range()
        => Assert.Null(TaxiDateRange.Occupied(false, false, false, Start, 4, [1]));

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 5 })]
    [InlineData(new[] { 1, 9 })]
    public void Sightseeing_days_empty_or_outside_the_plan_have_no_range(int[] days)
        => Assert.Null(TaxiDateRange.Occupied(true, true, true, Start, 4, days));

    [Fact]
    public void A_plan_without_days_has_no_range()
        => Assert.Null(TaxiDateRange.Occupied(true, false, true, Start, 0, []));

    [Fact]
    public void Days_counts_both_ends() => Assert.Equal(4, new TaxiDateRange(D(1), D(4)).Days);

    [Theory]
    [InlineData(1, 4, 4, 6, true)]
    [InlineData(1, 4, 5, 6, false)]
    [InlineData(5, 6, 1, 4, false)]
    [InlineData(1, 10, 3, 4, true)]
    [InlineData(3, 4, 1, 10, true)]
    [InlineData(3, 3, 3, 3, true)]
    public void Overlap_is_inclusive_on_both_ends(int aFrom, int aTo, int bFrom, int bTo, bool expected)
    {
        Assert.Equal(expected, new TaxiDateRange(D(aFrom), D(aTo)).Overlaps(new TaxiDateRange(D(bFrom), D(bTo))));
        Assert.Equal(expected, TaxiDateRange.Overlaps(D(bFrom), D(bTo), D(aFrom), D(aTo)));
    }

    [Fact]
    public void Same_day_hand_over_overlaps_and_a_gap_of_one_day_does_not()
    {
        Assert.True(new TaxiDateRange(D(1), D(3)).Overlaps(new TaxiDateRange(D(3), D(5))));
        Assert.False(new TaxiDateRange(D(1), D(3)).Overlaps(new TaxiDateRange(D(4), D(5))));
    }

    [Fact]
    public void Contract_sample_dates_overlap_the_conflict_sample()
        => Assert.True(TaxiDateRange.Overlaps(new(2026, 12, 18), new(2026, 12, 21), new(2026, 12, 20), new(2026, 12, 24)));
}
