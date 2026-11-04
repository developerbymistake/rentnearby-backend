namespace RentNearBy.Tests.Trips;

public class JourneyLeadTextTests
{
    private static readonly DateOnly D = new(2026, 11, 1);

    [Fact]
    public void Multi_car_text_has_prefix_and_comes_from()
    {
        var t = JourneyLeadText.Build("Haridwar", "Badrinath", D, 4, 2, 2, "Delhi");
        Assert.StartsWith("Car 2 of 2: Haridwar to Badrinath, 01 Nov,", t);
        Assert.EndsWith("Comes from Delhi with another driver.", t);
    }

    [Fact]
    public void First_car_has_no_comes_from()
    {
        var t = JourneyLeadText.Build("Delhi", "Haridwar", D, 4, 1, 2, null);
        Assert.StartsWith("Car 1 of 2: Delhi to Haridwar, 01 Nov,", t);
        Assert.DoesNotContain("Comes from", t);
    }

    [Fact]
    public void Several_vehicles_for_the_group_are_named()
    {
        var t = JourneyLeadText.Build("Delhi", "Badrinath", D, 5, 1, 1, null, cars: 2);
        Assert.Equal("Delhi to Badrinath, 01 Nov, 5 people, 2 cars.", t);
        Assert.Equal("Delhi to Badrinath, 01 Nov, 5 people.", JourneyLeadText.Build("Delhi", "Badrinath", D, 5, 1, 1, null));
    }

    [Fact]
    public void Single_car_has_no_prefix()
    {
        var t = JourneyLeadText.Build("Delhi", "Badrinath", D, 2, 1, 1, null);
        Assert.StartsWith("Delhi to Badrinath, 01 Nov,", t);
        Assert.DoesNotContain("Car ", t);
    }

    [Fact]
    public void Pickup_place_names_the_car_and_fits_80()
    {
        var p = JourneyLeadText.PickupPlace("Haridwar", 2, 2);
        Assert.Equal("Haridwar · car 2 of 2", p);
        Assert.True(JourneyLeadText.PickupPlace(new string('x', 200), 2, 3).Length <= 80);
    }

    [Fact]
    public void Car_prefix_round_trips()
    {
        var t = JourneyLeadText.Build("A", "B", D, 2, 2, 3, "C");
        Assert.True(JourneyLeadText.TryParseCar(t, out var i, out var n));
        Assert.Equal(2, i);
        Assert.Equal(3, n);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Delhi to Badrinath")]
    [InlineData("Car x of 2: nope")]
    public void Text_without_prefix_does_not_parse(string text) =>
        Assert.False(JourneyLeadText.TryParseCar(text, out _, out _));
}
