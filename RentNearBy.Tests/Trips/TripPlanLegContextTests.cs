namespace RentNearBy.Tests.Trips;

public class TripPlanLegContextTests
{
    private static TripPlan TwoLegs() => new()
    {
        CityKey = 210000123,
        CityName = "Haridwar",
        Legs =
        [
            new TripCityLeg { Index = 1, CityKey = 210000123, CityName = "Haridwar", Latitude = 29.95, Longitude = 78.16 },
            new TripCityLeg { Index = 2, CityKey = 210000456, CityName = "Badrinath", Latitude = 30.74, Longitude = 79.49 },
        ],
        Days =
        [
            new TripDay { Day = 1, Leg = 1 },
            new TripDay { Day = 2, Leg = 2 },
            new TripDay { Day = 3, Leg = 2 },
            new TripDay { Day = 4, Leg = 2 },
        ],
    };

    private static TripPlan OneLeg()
    {
        var plan = new TripPlan { CityKey = 210000123, CityName = "Haridwar", Days = [new TripDay { Day = 1 }, new TripDay { Day = 2 }] };
        plan.Normalize();
        return plan;
    }

    [Fact]
    public void Leg_helpers_follow_the_days()
    {
        var plan = TwoLegs();

        Assert.Equal("Badrinath", plan.LegOf(2)!.CityName);
        Assert.Equal("Haridwar", plan.LegOf(1)!.CityName);
        Assert.Equal(1, plan.LegIndexOf(1));
        Assert.Equal(2, plan.LegIndexOf(4));
        Assert.Equal(1, plan.LegStart(1));
        Assert.Equal(1, plan.LegEnd(1));
        Assert.Equal(2, plan.LegStart(2));
        Assert.Equal(4, plan.LegEnd(2));
        Assert.Equal(3, plan.DaysOf(2).Count());
        Assert.Equal(new[] { "Haridwar", "Badrinath" }, plan.LegCities);
    }

    [Fact]
    public void Travel_day_is_the_first_day_of_leg_two_only()
    {
        var plan = TwoLegs();

        Assert.False(plan.IsTravelDay(1));
        Assert.True(plan.IsTravelDay(2));
        Assert.False(plan.IsTravelDay(3));
        Assert.False(plan.IsTravelDay(9));
    }

    [Fact]
    public void Stay_context_on_leg_two_names_that_city_with_index_and_count()
    {
        var ctx = new TripEnquiryContext();

        TripEnquiryCity.Apply(ctx, TwoLegs(), 3, "Haridwar", 4);

        Assert.Equal("Badrinath", ctx.City);
        Assert.Equal(2, ctx.CityIndex);
        Assert.Equal(2, ctx.CityCount);
        Assert.Equal(4, ctx.TripDayCount);
        var text = TripEnquiryText.Build(ctx, new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 4), false);
        Assert.StartsWith("Hi, trip to Badrinath", text);
        Assert.EndsWith(" Part of a 4-day trip, 2 of 2 cities.", text);
    }

    [Fact]
    public void Taxi_context_on_leg_one_names_the_first_city()
    {
        var ctx = new TripEnquiryContext();

        TripEnquiryCity.Apply(ctx, TwoLegs(), 1, "Haridwar", 4);

        Assert.Equal("Haridwar", ctx.City);
        Assert.Equal(1, ctx.CityIndex);
        Assert.Equal(2, ctx.CityCount);
        Assert.EndsWith(" Part of a 4-day trip, 1 of 2 cities.", TripEnquiryText.BuildLead(ctx, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2)));
    }

    [Fact]
    public void One_city_context_keeps_the_city_fields_empty()
    {
        var ctx = new TripEnquiryContext();

        TripEnquiryCity.Apply(ctx, OneLeg(), 2, "Haridwar", 2);

        Assert.Equal("Haridwar", ctx.City);
        Assert.Null(ctx.CityIndex);
        Assert.Null(ctx.CityCount);
        Assert.Null(ctx.TripDayCount);
        Assert.DoesNotContain("Part of a", TripEnquiryText.Build(ctx, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3), false));
    }

    [Fact]
    public void Legless_plan_falls_back_to_the_row_city()
    {
        var ctx = new TripEnquiryContext();

        TripEnquiryCity.Apply(ctx, new TripPlan(), 1, "Nainital", 3);

        Assert.Equal("Nainital", ctx.City);
        Assert.Null(ctx.CityCount);
    }
}
