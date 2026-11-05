namespace RentNearBy.Tests.Trips;

public class TaxiSeatCoverTests
{
    private static SeatOption Car(int rank, short? seats, int fare) => new(rank, seats, fare);

    [Fact]
    public void Ten_people_split_over_a_seven_and_a_four_seater_not_three_small_cars()
    {
        var r = TaxiSeatCover.Solve([Car(0, 4, 6000), Car(1, 7, 9000)], 10);

        Assert.True(r.Covered);
        Assert.Equal(new[] { 0, 1 }, r.Chosen);
        Assert.Equal(15000, r.Fare);
        Assert.Equal(2, r.Cars);
    }

    [Fact]
    public void Six_people_take_two_distinct_four_seaters()
    {
        var r = TaxiSeatCover.Solve([Car(0, 4, 6000), Car(1, 4, 6500)], 6);

        Assert.Equal(new[] { 0, 1 }, r.Chosen);
        Assert.Equal(12500, r.Fare);
    }

    [Fact]
    public void A_top_car_that_fits_alone_is_kept()
    {
        var r = TaxiSeatCover.Solve([Car(0, 4, 3000), Car(1, 6, 1000)], 2);

        Assert.Equal(new[] { 0 }, r.Chosen);
        Assert.Equal((3000, 1, true), (r.Fare, r.Cars, r.Covered));
    }

    [Fact]
    public void Unknown_seats_never_count_towards_the_cover()
    {
        var r = TaxiSeatCover.Solve([Car(0, null, 1000), Car(1, 4, 3000), Car(2, 2, 2000)], 5);

        Assert.Equal(new[] { 1, 2 }, r.Chosen);
        Assert.Equal(5000, r.Fare);
    }

    [Fact]
    public void Three_cheap_cars_beat_two_dear_ones_on_fare()
    {
        var r = TaxiSeatCover.Solve([Car(0, 6, 9000), Car(1, 4, 3000), Car(2, 2, 3000), Car(3, 2, 2000)], 8);

        Assert.Equal(new[] { 1, 2, 3 }, r.Chosen);
        Assert.Equal((8000, 3), (r.Fare, r.Cars));
    }

    [Fact]
    public void Equal_fares_prefer_fewer_cars_then_the_better_ranked_set()
    {
        var fewer = TaxiSeatCover.Solve([Car(0, 2, 1000), Car(1, 2, 1000), Car(2, 4, 2000)], 4);
        var ranked = TaxiSeatCover.Solve([Car(0, 2, 1000), Car(1, 2, 1000), Car(2, 2, 1000)], 4);

        Assert.Equal(new[] { 2 }, fewer.Chosen);
        Assert.Equal(new[] { 0, 1 }, ranked.Chosen);
    }

    [Fact]
    public void No_cover_falls_back_to_the_top_car_per_started_group()
    {
        var r = TaxiSeatCover.Solve([Car(0, 4, 6000)], 10);

        Assert.False(r.Covered);
        Assert.Equal(new[] { 0 }, r.Chosen);
        Assert.Equal((18000, 3), (r.Fare, r.Cars));
    }

    [Fact]
    public void Empty_and_unknown_group()
    {
        Assert.Same(SeatCoverResult.None, TaxiSeatCover.Solve([], 4));
        var r = TaxiSeatCover.Solve([Car(0, 4, 2000), Car(1, 7, 1000)], 0);
        Assert.Equal(new[] { 0 }, r.Chosen);
        Assert.Equal(2000, r.Fare);
    }

    [Fact]
    public void A_plan_leg_splits_the_group_over_real_distinct_cars()
    {
        var s = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Haridwar", TaxiWaysFixture.Haridwar, 6000, seats: 4),
            TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Haridwar", TaxiWaysFixture.Haridwar, 9000, seats: 7),
            TaxiWaysFixture.Row("Haridwar", TaxiWaysFixture.Haridwar, "Badrinath", TaxiWaysFixture.Badrinath, 5000, seats: 12));

        var r = TaxiWaysFixture.Search(s, TaxiWaysFixture.Delhi, TaxiWaysFixture.Badrinath, people: 10);

        var plan = Assert.Single(r.Plans);
        var leg = plan.Legs[0];
        Assert.Equal(2, leg.CarsNeeded);
        Assert.Equal(15000, leg.ListedFare);
        Assert.Equal(2, leg.CoverDrivers.Select(c => c.Driver.TaxiId).Distinct().Count());
        Assert.Equal("Seats 4 + 7 · 2 cars for 10", leg.SeatsNote);
        Assert.Equal(20000, plan.Total);
    }
}
