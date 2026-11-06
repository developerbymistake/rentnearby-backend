using System.Text.Json;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Trips;

// PlanJson v5 round trip, own step travel, going home to, the steps validator and TripPlanMerge.KeepStored for an older app.
public class TripPlanV5Tests
{
    private static readonly SaveTripRequestValidator Validator = new();
    private static readonly Guid Enquiry = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Journey = Guid.Parse("99999999-2222-3333-4444-555555555555");

    private static TripPlan Plan() => new()
    {
        CityKey = 502141,
        CityName = "Nainital",
        CentreLatitude = 29.39,
        CentreLongitude = 79.45,
        Days =
        {
            new TripDay { Day = 1, RestDay = true },
            new TripDay { Day = 2, Stay = new TripStayChoice { Name = "Night", Skipped = true, LeaveMoved = true } },
        },
        Settings = new TripSettings { StartDate = new DateOnly(2026, 11, 2) },
    };

    private static TripPlan RoundTrip(TripPlan plan)
        => JsonSerializer.Deserialize<TripPlan>(JsonSerializer.Serialize(plan, TripJson.Options), TripJson.Options)!;

    private static SaveTripRequest Save(TripSteps steps)
    {
        var plan = Plan();
        plan.Steps = steps;
        return new SaveTripRequest { Plan = plan };
    }

    private static TripStartFrom Jaipur() => new() { Name = "Jaipur", Lat = 26.91, Lon = 75.79 };

    private static TripStep Own() => new()
    {
        Status = TripStepStatuses.Own, How = TripTravelHows.Car, OwnName = "UK07 1234", OwnPhone = "9876543210", LeavesAt = "06:30",
    };

    [Fact]
    public void RoundTrip_V5_KeepsEveryNewField()
    {
        var plan = Plan();
        plan.DismissedEnquiryIds = [Enquiry];
        plan.Steps = new TripSteps
        {
            StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 },
            GoingHomeTo = Jaipur(),
            GettingThere = Own(),
            ComingBack = new TripStep
            {
                Status = TripStepStatuses.Chosen, JourneyId = Journey, OwnCars = [1, 3], LeaveOn = TripLeaveOn.LastDayEvening,
                Date = new DateOnly(2026, 11, 4),
            },
        };

        var back = RoundTrip(plan);

        Assert.Equal(5, back.Version);
        Assert.Equal([Enquiry], back.DismissedEnquiryIds);
        Assert.True(back.Days[0].RestDay);
        Assert.True(back.Days[1].Stay!.Skipped);
        Assert.True(back.Days[1].Stay!.LeaveMoved);
        Assert.Equal("Jaipur", back.Steps!.GoingHomeTo!.Name);
        Assert.Equal(26.91, back.Steps.GoingHomeTo.Lat);
        Assert.Equal("Delhi", back.Steps.StartFrom!.Name);
        var g = back.Steps.GettingThere!;
        Assert.Equal((TripTravelHows.Car, "UK07 1234", "9876543210", "06:30"), (g.How, g.OwnName, g.OwnPhone, g.LeavesAt));
        var c = back.Steps.ComingBack!;
        Assert.Equal([1, 3], c.OwnCars);
        Assert.Equal(TripLeaveOn.LastDayEvening, c.LeaveOn);
        Assert.Equal(Journey, c.JourneyId);
        Assert.Equal(new DateOnly(2026, 11, 4), c.Date);
    }

    [Fact]
    public void Serialize_OmitsAbsentOwnFieldsAndGoingHome()
    {
        var plan = Plan();
        plan.Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = Journey } };

        var json = JsonSerializer.Serialize(plan, TripJson.Options);

        Assert.DoesNotContain("goingHomeTo", json);
        Assert.DoesNotContain("ownName", json);
        Assert.DoesNotContain("leavesAt", json);
        Assert.Contains("\"journeyId\"", json);
    }

    [Fact]
    public void Deserialize_OldPlanWithoutGoingHomeOrOwnFields_Loads()
    {
        var json = "{\"version\":4,\"cityKey\":502141,\"cityName\":\"Nainital\",\"days\":[{\"day\":1,\"spots\":[]}],"
                   + "\"steps\":{\"startFrom\":{\"name\":\"Delhi\",\"lat\":28.61,\"lon\":77.21},"
                   + "\"gettingThere\":{\"status\":\"own\",\"note\":\"Own car\"}}}";

        var plan = JsonSerializer.Deserialize<TripPlan>(json, TripJson.Options)!;

        Assert.Equal(4, plan.Version);
        Assert.Null(plan.Steps!.GoingHomeTo);
        Assert.Equal("Own car", plan.Steps.GettingThere!.Note);
        Assert.Null(plan.Steps.GettingThere.How);
        Assert.Null(plan.Steps.GettingThere.LeavesAt);
    }

    [Fact]
    public void Normalize_NotNeededStepBecomesNotStarted()
    {
        var plan = Plan();
        plan.Steps = new TripSteps
        {
            GettingThere = new TripStep { Status = TripStepStatuses.NotNeeded },
            ComingBack = new TripStep { Status = TripStepStatuses.Own },
        };

        plan.Normalize(502141, "Nainital", 29.39, 79.45);

        Assert.Equal(TripStepStatuses.NotStarted, plan.Steps.GettingThere!.Status);
        Assert.Equal(TripStepStatuses.Own, plan.Steps.ComingBack!.Status);
    }

    [Fact]
    public void ComingBackDate_FollowsLeaveOn()
    {
        var plan = Plan();
        plan.Days.Add(new TripDay { Day = 3 });

        Assert.Equal(new DateOnly(2026, 11, 5), plan.ComingBackDate);

        plan.Steps = new TripSteps { ComingBack = new TripStep { LeaveOn = TripLeaveOn.LastDayEvening } };

        Assert.Equal(new DateOnly(2026, 11, 4), plan.ComingBackDate);
    }

    [Fact]
    public void Save_OwnFieldsOnOwnStepAndGoingHome_Valid()
    {
        var request = Save(new TripSteps { GoingHomeTo = Jaipur(), GettingThere = Own(), ComingBack = Own() });

        ValidationAssert.Valid(Validator.Validate(request));
    }

    [Fact]
    public void Save_OwnFieldsOnNonOwnStep_Invalid()
    {
        var step = Own();
        step.Status = TripStepStatuses.Chosen;

        ValidationAssert.HasErrorOn(Validator.Validate(Save(new TripSteps { ComingBack = step })), "Plan.Steps.ComingBack");
    }

    [Theory]
    [InlineData("plane")]
    [InlineData("")]
    public void Save_BadHow_Invalid(string how)
    {
        var step = Own();
        step.How = how;

        ValidationAssert.HasErrorOn(Validator.Validate(Save(new TripSteps { GettingThere = step })), "Plan.Steps.GettingThere");
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("5876543210")]
    public void Save_BadOwnPhone_Invalid(string phone)
    {
        var step = Own();
        step.OwnPhone = phone;

        ValidationAssert.HasErrorOn(Validator.Validate(Save(new TripSteps { GettingThere = step })), "Plan.Steps.GettingThere");
    }

    [Fact]
    public void Save_BadLeavesAtAndLongName_Invalid()
    {
        var time = Own();
        time.LeavesAt = "25:00";
        var name = Own();
        name.OwnName = new string('a', 81);

        ValidationAssert.HasErrorOn(Validator.Validate(Save(new TripSteps { GettingThere = time })), "Plan.Steps.GettingThere");
        ValidationAssert.HasErrorOn(Validator.Validate(Save(new TripSteps { ComingBack = name })), "Plan.Steps.ComingBack");
    }

    [Theory]
    [InlineData("", 26.91, 75.79)]
    [InlineData("  ", 26.91, 75.79)]
    [InlineData("Jaipur", 91, 75.79)]
    [InlineData("Jaipur", 26.91, 181)]
    public void Save_BadGoingHomeTo_Invalid(string name, double lat, double lon)
    {
        var request = Save(new TripSteps { GoingHomeTo = new TripStartFrom { Name = name, Lat = lat, Lon = lon } });

        ValidationAssert.HasErrorOn(Validator.Validate(request), "Plan.Steps");
    }

    [Fact]
    public void Save_GoingHomeToName81_Invalid()
    {
        var request = Save(new TripSteps { GoingHomeTo = new TripStartFrom { Name = new string('a', 81), Lat = 26.91, Lon = 75.79 } });

        ValidationAssert.HasMessage(Validator.Validate(request), "Going home to must be 1 to 80 characters");
    }

    [Fact]
    public void KeepStored_V4Request_KeepsRestDaysLeaveOnOwnFieldsAndGoingHome()
    {
        var stored = Plan();
        stored.DismissedEnquiryIds = [Enquiry];
        stored.Steps = new TripSteps
        {
            GoingHomeTo = Jaipur(),
            GettingThere = Own(),
            ComingBack = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = Journey, OwnCars = [2], LeaveOn = TripLeaveOn.LastDayEvening },
        };
        var request = new TripPlan
        {
            Version = 4,
            Days = { new TripDay { Day = 1 }, new TripDay { Day = 2 } },
            Steps = new TripSteps
            {
                GettingThere = new TripStep { Status = TripStepStatuses.Own },
                ComingBack = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = Journey },
            },
        };

        TripPlanMerge.KeepStored(request, stored, 4);

        Assert.Equal([Enquiry], request.DismissedEnquiryIds);
        Assert.True(request.Days[0].RestDay);
        Assert.Null(request.Days[1].RestDay);
        Assert.Equal("Jaipur", request.Steps!.GoingHomeTo!.Name);
        var g = request.Steps.GettingThere!;
        Assert.Equal((TripTravelHows.Car, "UK07 1234", "9876543210", "06:30"), (g.How, g.OwnName, g.OwnPhone, g.LeavesAt));
        Assert.Equal([2], request.Steps.ComingBack!.OwnCars);
        Assert.Equal(TripLeaveOn.LastDayEvening, request.Steps.ComingBack.LeaveOn);
    }

    [Fact]
    public void KeepStored_V5Request_ClearingGoingHomeIsRespected()
    {
        var stored = Plan();
        stored.DismissedEnquiryIds = [Enquiry];
        stored.Steps = new TripSteps { GoingHomeTo = Jaipur() };
        var request = new TripPlan { Version = 5, Days = { new TripDay { Day = 1 } }, Steps = new TripSteps() };

        TripPlanMerge.KeepStored(request, stored, 5);

        Assert.Equal([Enquiry], request.DismissedEnquiryIds);
        Assert.Null(request.Days[0].RestDay);
        Assert.Null(request.Steps!.GoingHomeTo);
    }

    [Fact]
    public void KeepStored_V4Request_DoesNotOverwriteOwnFieldsTheAppSent()
    {
        var stored = Plan();
        stored.Steps = new TripSteps { GettingThere = Own() };
        var request = new TripPlan
        {
            Version = 4,
            Days = { new TripDay { Day = 1 } },
            Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Own, OwnName = "Bus 12" } },
        };

        TripPlanMerge.KeepStored(request, stored, 4);

        Assert.Equal("Bus 12", request.Steps!.GettingThere!.OwnName);
        Assert.Null(request.Steps.GettingThere.OwnPhone);
    }
}
