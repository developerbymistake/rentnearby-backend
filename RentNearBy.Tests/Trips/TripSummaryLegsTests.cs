namespace RentNearBy.Tests.Trips;

// Per-city grouping of nights and taxi days for the Summary record.
public class TripSummaryLegsTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);

    private static TripPlan Plan(params int[] legOfDay)
    {
        var legCount = legOfDay.Length == 0 ? 1 : legOfDay.Max();
        var names = new[] { "Haridwar", "Badrinath", "Joshimath", "Auli" };
        var plan = new TripPlan
        {
            CityKey = 210000001,
            CityName = names[0],
            Settings = new TripSettings { StartDate = Start },
            Legs = Enumerable.Range(1, legCount)
                .Select(i => new TripCityLeg { Index = i, CityKey = 210000000 + i, CityName = names[i - 1], Latitude = 30, Longitude = 78 })
                .ToList(),
        };
        for (var d = 1; d <= legOfDay.Length; d++)
            plan.Days.Add(new TripDay { Day = d, Leg = legOfDay[d - 1] });
        return plan;
    }

    [Fact]
    public void Local_taxi_inside_one_leg_belongs_to_that_leg()
    {
        var plan = Plan(1, 2, 2, 2);
        Assert.Equal(new List<int> { 2 }, TripSummaryLegs.LegOfTaxi(plan, Start, [3, 4], Start.AddDays(2)));
        Assert.Equal(new List<int> { 1, 2 }, TripSummaryLegs.LegOfTaxi(plan, null, [1, 2], null));
        Assert.Equal(new List<int> { 2 }, TripSummaryLegs.LegOfTaxi(plan, Start, [], Start.AddDays(1)));
    }

    [Fact]
    public void Taxi_without_a_date_or_days_falls_back_to_the_first_leg()
        => Assert.Equal(new List<int> { 1 }, TripSummaryLegs.LegOfTaxi(Plan(1, 2), null, [], null));

    [Fact]
    public void Night_belongs_to_the_leg_of_its_day()
    {
        var plan = Plan(1, 2, 2, 2);
        Assert.Equal(1, TripSummaryLegs.LegOfNight(plan, 1));
        Assert.Equal(2, TripSummaryLegs.LegOfNight(plan, 4));
        Assert.Equal(new HashSet<int> { 2, 3, 4 }, TripSummaryLegs.LegDays(plan, 2));
    }
}
