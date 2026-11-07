namespace RentNearBy.Tests.Trips;

// Own arrangements are optional: values are normalised or dropped, never refused.
public class TripOwnRulesTests
{
    [Theory]
    [InlineData("9876543210", "9876543210")]
    [InlineData("+91 98765 43210", "9876543210")]
    [InlineData("919876543210", "9876543210")]
    [InlineData("09876543210", "9876543210")]
    [InlineData("98765-43210", "9876543210")]
    [InlineData("5876543210", null)]
    [InlineData("12345", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Phone_NormalisedOrDropped(string? raw, string? expected)
        => Assert.Equal(expected, TripOwnRules.Phone(raw));

    [Fact]
    public void Text_TrimsTruncatesAndBlanksToNull()
    {
        Assert.Null(TripOwnRules.Text("   ", 80));
        Assert.Equal("Volvo", TripOwnRules.Text("  Volvo ", 80));
        Assert.Equal(80, TripOwnRules.Text(new string('a', 120), 80)!.Length);
    }

    [Theory]
    [InlineData("flight", "flight")]
    [InlineData("car", "car")]
    [InlineData("plane", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void How_KnownOnly(string? raw, string? expected)
        => Assert.Equal(expected, TripOwnRules.How(raw));

    [Theory]
    [InlineData("06:30", "06:30")]
    [InlineData("25:00", null)]
    [InlineData("6:30", null)]
    public void LeavesAt_ValidOnly(string raw, string? expected)
        => Assert.Equal(expected, TripOwnRules.LeavesAt(raw));

    [Fact]
    public void CleanStep_NotOwn_ClearsOwnFields()
    {
        var step = new TripStep { Status = TripStepStatuses.Chosen, How = "bus", OwnName = "x", OwnPhone = "9876543210", LeavesAt = "06:30" };

        TripOwnRules.Clean(step);

        Assert.Equal((null, null, null, null), (step.How, step.OwnName, step.OwnPhone, step.LeavesAt));
    }

    [Fact]
    public void Normalize_CleansOwnTaxiStayAndArrival()
    {
        var plan = new TripPlan
        {
            CityKey = 1,
            CityName = "Nainital",
            Days =
            {
                new TripDay
                {
                    Day = 1,
                    Taxi = new TripTaxiChoice { IsOwn = true, Name = "  ", OwnPhone = "+91 98765 43210", Note = " " },
                    Stay = new TripStayChoice { IsOwn = true, Name = new string('h', 100), OwnPhone = "123" },
                },
            },
        };

        plan.Normalize();

        var day = plan.Days[0];
        Assert.Equal(string.Empty, day.Taxi!.Name);
        Assert.Equal("9876543210", day.Taxi.OwnPhone);
        Assert.Null(day.Taxi.Note);
        Assert.Equal(80, day.Stay!.Name.Length);
        Assert.Null(day.Stay.OwnPhone);
    }
}
