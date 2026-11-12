using RentNearBy.Api.Handlers;

namespace RentNearBy.Tests.Trips;

// A send with no stepKey is gated on the step the server derives from the request.
public class TripGuardsDerivedStepTests
{
    private static TripPlan TwoCityPlan() => new()
    {
        Legs = [new TripCityLeg { Index = 1, CityName = "A" }, new TripCityLeg { Index = 2, CityName = "B" }],
        Days = [new TripDay { Day = 1, Leg = 1 }, new TripDay { Day = 2, Leg = 1 }, new TripDay { Day = 3, Leg = 2 }],
    };

    [Theory]
    [InlineData(1, "stay:1")]
    [InlineData(2, "stay:1")]
    [InlineData(3, "stay:2")]
    public void Stay_step_is_the_city_of_the_nights(int day, string expected)
        => Assert.Equal(expected, TripGuards.DerivedStayStep(TwoCityPlan(), day));

    [Fact]
    public void Own_stay_mark_closes_the_derived_step_and_an_open_step_stays_open()
    {
        var plan = TwoCityPlan();
        plan.Legs![1].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
        Assert.True(TripGuards.StepClosed(plan, TripGuards.DerivedStayStep(plan, 3)));
        Assert.False(TripGuards.StepClosed(plan, TripGuards.DerivedStayStep(plan, 1)));
    }

    [Fact]
    public void Skipped_taxi_mark_closes_the_taxi_step()
    {
        var plan = TwoCityPlan();
        plan.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };
        Assert.True(TripGuards.StepClosed(plan, TripRowKinds.Taxi));
    }

    [Theory]
    [InlineData(TripStepRoles.GettingThere, 0, "gettingThere")]
    [InlineData(TripStepRoles.ComingBack, 0, "comingBack")]
    [InlineData(TripStepRoles.Segment, 2, "between:2")]
    [InlineData("other", 2, null)]
    public void Journey_step_follows_the_plan_role(string role, int leg, string? expected)
        => Assert.Equal(expected, TripGuards.DerivedJourneyStep(role, leg));

    [Fact]
    public void Own_getting_there_closes_the_journey_step()
    {
        var plan = TwoCityPlan();
        plan.Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Own } };
        Assert.True(TripGuards.StepClosed(plan, TripGuards.DerivedJourneyStep(TripStepRoles.GettingThere, 0)!));
        Assert.False(TripGuards.StepClosed(plan, TripGuards.DerivedJourneyStep(TripStepRoles.ComingBack, 0)!));
    }
}
