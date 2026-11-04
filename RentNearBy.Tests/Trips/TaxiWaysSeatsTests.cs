namespace RentNearBy.Tests.Trips;

public class TaxiWaysSeatsTests
{
    private static readonly (double Lat, double Lng) Delhi = TaxiWaysFixture.Delhi;
    private static readonly (double Lat, double Lng) Haridwar = TaxiWaysFixture.Haridwar;
    private static readonly (double Lat, double Lng) Badrinath = TaxiWaysFixture.Badrinath;

    [Theory]
    [InlineData(1, 4, 1)]
    [InlineData(4, 4, 1)]
    [InlineData(5, 4, 2)]
    [InlineData(8, 4, 2)]
    [InlineData(9, 4, 3)]
    [InlineData(5, 7, 1)]
    [InlineData(12, 6, 2)]
    public void Cars_needed_is_one_car_per_started_group_of_seats(int people, int seats, int expected)
        => Assert.Equal(expected, TaxiWaysRanking.CarsNeeded(people, (short)seats));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Unknown_people_or_seats_means_one_car(int people)
    {
        Assert.Equal(1, TaxiWaysRanking.CarsNeeded(people, 4));
        Assert.Equal(1, TaxiWaysRanking.CarsNeeded(5, null));
        Assert.Equal(1, TaxiWaysRanking.CarsNeeded(5, 0));
    }

    [Fact]
    public void Seats_note_only_when_more_than_one_car()
    {
        Assert.Equal("Seats 4 · 2 cars for 5", TaxiWaysRanking.SeatsNote(5, 4));
        Assert.Null(TaxiWaysRanking.SeatsNote(4, 4));
        Assert.Null(TaxiWaysRanking.SeatsNote(5, null));
    }

    [Fact]
    public void A_plan_leg_multiplies_the_fare_by_the_cars_it_needs()
    {
        var s = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Delhi", Delhi, "Haridwar", Haridwar, 4000, seats: 4),
            TaxiWaysFixture.Row("Haridwar", Haridwar, "Badrinath", Badrinath, 9000, seats: 7));
        var r = TaxiWaysFixture.Search(s, Delhi, Badrinath, people: 5);

        Assert.NotEmpty(r.Plans);
        var plan = r.Plans[0];
        Assert.Equal(2, plan.Cars);
        Assert.Equal(2, plan.Legs[0].CarsNeeded);
        Assert.Equal(8000, plan.Legs[0].ListedFare);
        Assert.Equal("Seats 4 · 2 cars for 5", plan.Legs[0].SeatsNote);
        Assert.Equal(1, plan.Legs[1].CarsNeeded);
        Assert.Equal(9000, plan.Legs[1].ListedFare);
        Assert.Null(plan.Legs[1].SeatsNote);
        Assert.Equal(17000, plan.Total);
    }

    [Fact]
    public void Lead_text_names_the_cars_for_the_group()
    {
        var text = JourneyLeadText.Build("Delhi", "Haridwar", new DateOnly(2026, 11, 1), 5, 1, 2, null, cars: 2);
        Assert.StartsWith("Car 1 of 2: Delhi to Haridwar, 01 Nov, 5 people, 2 cars.", text);
    }
}
