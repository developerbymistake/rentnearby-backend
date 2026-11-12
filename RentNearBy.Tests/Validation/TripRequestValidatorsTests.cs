using FluentValidation.TestHelper;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Validation;

public class TripRequestValidatorsTests
{
    private static readonly PlanTripRequestValidator PlanValidator = new();
    private static readonly SaveTripRequestValidator SaveValidator = new();
    private const long CityKey = 502141;

    private static PlanSpotInput Input(int i = 0) => new()
    {
        Id = $"osm:N{i}", Name = $"Place {i}", Latitude = 29.39, Longitude = 79.45, Category = "attraction", VisitMinutes = 60,
    };

    private static TripSpot Spot(int i = 0) => new()
    {
        Id = $"osm:N{i}", Name = $"Place {i}", Latitude = 29.39, Longitude = 79.45, Category = "attraction", VisitMinutes = 60,
    };

    private static PlanTripRequest Auto(int spots = 2) => new()
    {
        CityKey = CityKey, Mode = TripPlanModes.Auto, Spots = Enumerable.Range(0, spots).Select(Input).ToList(),
    };

    private static PlanTripRequest Recalc(params int[] spotsPerDay) => new()
    {
        CityKey = CityKey,
        Mode = TripPlanModes.Recalculate,
        Days = spotsPerDay.Select((count, d) => new PlanDayInput
        {
            Day = d + 1,
            Spots = Enumerable.Range(d * 100, count).Select(Input).ToList(),
        }).ToList(),
    };

    private static SaveTripRequest Save(string? status = null, params int[] spotsPerDay) => new()
    {
        Status = status,
        Plan = new TripPlan
        {
            CityKey = CityKey,
            Legs = [new TripCityLeg { Index = 1, CityKey = CityKey, CityName = "Nainital", Latitude = 29.38, Longitude = 79.45 }],
            Days = (spotsPerDay.Length == 0 ? new[] { 1 } : spotsPerDay).Select((count, d) => new TripDay
            {
                Day = d + 1,
                Spots = Enumerable.Range(d * 100, count).Select(Spot).ToList(),
            }).ToList(),
        },
    };

    // ---- PlanTripRequestValidator ----

    [Fact]
    public void Plan_AutoDefaults_Valid()
        => PlanValidator.TestValidate(Auto()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Plan_RecalculateDefaults_Valid()
        => PlanValidator.TestValidate(Recalc(2, 1)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Plan_EmptyCity_Invalid()
    {
        var request = Auto();
        request.CityKey = 0;

        PlanValidator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.CityKey);
    }

    [Fact]
    public void Plan_StartDateOutOfRange_Invalid()
    {
        var request = Auto();
        request.StartDate = new DateOnly(9999, 12, 31);

        ValidationAssert.HasMessage(PlanValidator.Validate(request), "Invalid start date");
    }

    [Fact]
    public void Save_StartDateOutOfRange_Invalid()
    {
        var request = Save();
        request.Plan.Settings.StartDate = new DateOnly(9999, 12, 31);

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Invalid start date");
    }

    [Theory]
    [InlineData("")]
    [InlineData("AUTO")]
    [InlineData("split")]
    public void Plan_UnknownMode_Invalid(string mode)
    {
        var request = Auto();
        request.Mode = mode;

        PlanValidator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Mode);
    }

    [Fact]
    public void Plan_AutoNoSpots_Invalid()
    {
        ValidationAssert.HasMessage(PlanValidator.Validate(Auto(0)), PlanTripRequestValidator.NoPlacesMessage);

        var request = Auto();
        request.Spots = null;
        ValidationAssert.HasMessage(PlanValidator.Validate(request), PlanTripRequestValidator.NoPlacesMessage);
    }

    [Fact]
    public void Plan_AutoSixtySpots_Valid()
        => PlanValidator.TestValidate(Auto(60)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Plan_AutoSixtyOneSpots_TooManySpots()
    {
        var result = PlanValidator.Validate(Auto(61));

        ValidationAssert.HasMessage(result, "A trip can have up to 60 places");
        Assert.Contains(result.Errors, e => e.ErrorCode == TripErrorTypes.TooManySpots);
    }

    [Fact]
    public void Plan_OptionLowersSpotCap()
    {
        var validator = new PlanTripRequestValidator(Options.Create(new TripPlannerOptions { MaxSpotsPerTrip = 10 }));

        ValidationAssert.HasMessage(validator.Validate(Auto(11)), "A trip can have up to 10 places");
    }

    [Fact]
    public void Plan_OptionCannotRaiseSpotCap()
    {
        var validator = new PlanTripRequestValidator(Options.Create(new TripPlannerOptions { MaxSpotsPerTrip = 100 }));

        ValidationAssert.HasMessage(validator.Validate(Auto(61)), "A trip can have up to 60 places");
    }

    [Fact]
    public void Plan_AutoWithDays_Invalid()
    {
        var request = Auto();
        request.Days = new List<PlanDayInput>();

        PlanValidator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Days);
    }

    [Fact]
    public void Plan_RecalculateWithoutDays_Invalid()
    {
        var request = Recalc(1);
        request.Days = null;

        ValidationAssert.HasMessage(PlanValidator.Validate(request), PlanTripRequestValidator.NoPlacesMessage);
    }

    [Fact]
    public void Plan_RecalculateFifteenDays_Invalid()
        => ValidationAssert.HasMessage(PlanValidator.Validate(Recalc(Enumerable.Repeat(1, 15).ToArray())), PlanTripRequestValidator.TooManyDaysMessage);

    [Fact]
    public void Plan_RecalculateDayOutOfRange_Invalid()
    {
        var request = Recalc(1);
        request.Days![0].Day = 15;

        ValidationAssert.HasMessage(PlanValidator.Validate(request), PlanTripRequestValidator.TooManyDaysMessage);
    }

    [Fact]
    public void Plan_RecalculateDuplicateDay_Invalid()
    {
        var request = Recalc(1, 1);
        request.Days![1].Day = 1;

        ValidationAssert.HasMessage(PlanValidator.Validate(request), "Each day can appear only once");
    }

    [Fact]
    public void Plan_RecalculateEmptyDayAllowed()
        => PlanValidator.TestValidate(Recalc(2, 0)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Plan_RecalculateNoSpotsAtAll_Invalid()
        => ValidationAssert.HasMessage(PlanValidator.Validate(Recalc(0, 0)), PlanTripRequestValidator.NoPlacesMessage);

    [Fact]
    public void Plan_RecalculateSixtyOneSpots_TooManySpots()
        => ValidationAssert.HasMessage(PlanValidator.Validate(Recalc(Enumerable.Repeat(6, 11).ToArray())), "A trip can have up to 60 places");

    [Theory]
    [InlineData(91, 79.45)]
    [InlineData(-91, 79.45)]
    [InlineData(29.39, 181)]
    [InlineData(29.39, -181)]
    public void Plan_BadCoordinates_Invalid(double lat, double lon)
    {
        var request = Auto(1);
        request.Spots![0].Latitude = lat;
        request.Spots[0].Longitude = lon;

        ValidationAssert.HasMessage(PlanValidator.Validate(request), PlanTripRequestValidator.InvalidCoordinatesMessage);
    }

    [Fact]
    public void Plan_BadCoordinatesInDay_Invalid()
    {
        var request = Recalc(1);
        request.Days![0].Spots[0].Latitude = 95;

        ValidationAssert.HasMessage(PlanValidator.Validate(request), PlanTripRequestValidator.InvalidCoordinatesMessage);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(720, true)]
    [InlineData(721, false)]
    public void Plan_VisitMinutes(int minutes, bool valid)
    {
        var request = Auto(1);
        request.Spots![0].VisitMinutes = minutes;

        Assert.Equal(valid, PlanValidator.Validate(request).IsValid);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("osm:N1", true)]
    public void Plan_SpotId(string id, bool valid)
    {
        var request = Auto(1);
        request.Spots![0].Id = id;

        Assert.Equal(valid, PlanValidator.Validate(request).IsValid);
    }

    [Fact]
    public void Plan_SpotIdTooLong_Invalid()
    {
        var request = Auto(1);
        request.Spots![0].Id = new string('x', 65);

        Assert.False(PlanValidator.Validate(request).IsValid);
    }

    [Fact]
    public void Plan_SpotNameTooLong_Invalid()
    {
        var request = Auto(1);
        request.Spots![0].Name = new string('x', 121);

        Assert.False(PlanValidator.Validate(request).IsValid);
    }

    [Fact]
    public void Plan_CategoryTooLong_Invalid()
    {
        var request = Auto(1);
        request.Spots![0].Category = new string('x', 33);

        Assert.False(PlanValidator.Validate(request).IsValid);
    }

    [Theory]
    [InlineData(new[] { 1, 7 }, true)]
    [InlineData(new[] { 0 }, false)]
    [InlineData(new[] { 8 }, false)]
    public void Plan_ClosedDays(int[] days, bool valid)
    {
        var request = Auto(1);
        request.Spots![0].ClosedDays = days;

        Assert.Equal(valid, PlanValidator.Validate(request).IsValid);
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(900, true)]
    [InlineData(901, false)]
    public void Plan_DayLimitMinutes(int limit, bool valid)
    {
        var request = Auto();
        request.DayLimitMinutes = limit;

        Assert.Equal(valid, PlanValidator.Validate(request).IsValid);
    }

    // ---- SaveTripRequestValidator ----

    [Fact]
    public void Save_Defaults_Valid()
        => SaveValidator.TestValidate(Save()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_TitleTooLong_Invalid()
    {
        var request = Save();
        request.Title = new string('t', 81);

        SaveValidator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Theory]
    [InlineData("Draft", true)]
    [InlineData("Planned", true)]
    [InlineData("draft", false)]
    [InlineData("Done", false)]
    public void Save_Status(string status, bool valid)
        => Assert.Equal(valid, SaveValidator.Validate(Save(status)).IsValid);

    [Fact]
    public void Save_NullPlan_Invalid()
    {
        var request = Save();
        request.Plan = null!;

        SaveValidator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Plan);
    }

    [Fact]
    public void Save_WrongVersion_Invalid()
    {
        var request = Save();
        request.Plan.Version = 1;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Unsupported plan version");
    }

    [Fact]
    public void Save_ZeroCityKey_Invalid()
    {
        var request = Save();
        request.Plan.Legs![0].CityKey = 0;

        var result = SaveValidator.Validate(request);

        ValidationAssert.HasMessage(result, "City is required");
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Save_NoDays_Invalid()
    {
        var request = Save();
        request.Plan.Days.Clear();

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Add at least one day");
    }

    [Fact]
    public void Save_FifteenDays_Invalid()
        => ValidationAssert.HasMessage(SaveValidator.Validate(Save(null, Enumerable.Repeat(1, 15).ToArray())), PlanTripRequestValidator.TooManyDaysMessage);

    [Fact]
    public void Save_DaysOutOfOrder_Invalid()
    {
        var request = Save(null, 1, 1);
        request.Plan.Days[1].Day = 3;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Days must be numbered from 1 in order");
    }

    [Fact]
    public void Save_DraftWithoutPlaces_Valid()
        => SaveValidator.TestValidate(Save(TripStatuses.Draft, 0)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_NoStatusWithoutPlaces_Valid()
        => SaveValidator.TestValidate(Save(null, 0)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_PlannedWithoutPlaces_Invalid()
        => ValidationAssert.HasMessage(SaveValidator.Validate(Save(TripStatuses.Planned, 0)), PlanTripRequestValidator.NoPlacesMessage);

    [Fact]
    public void Save_SixtyOneSpots_TooManySpots()
    {
        var result = SaveValidator.Validate(Save(null, Enumerable.Repeat(6, 11).ToArray()));

        ValidationAssert.HasMessage(result, "A trip can have up to 60 places");
        Assert.Contains(result.Errors, e => e.ErrorCode == TripErrorTypes.TooManySpots);
    }

    [Fact]
    public void Save_BadSpotCoordinates_Invalid()
    {
        var request = Save();
        request.Plan.Days[0].Spots[0].Longitude = 200;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), PlanTripRequestValidator.InvalidCoordinatesMessage);
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(450, true)]
    [InlineData(901, false)]
    public void Save_DayLimitMinutes(int limit, bool valid)
    {
        var request = Save();
        request.Plan.Settings.DayLimitMinutes = limit;

        Assert.Equal(valid, SaveValidator.Validate(request).IsValid);
    }

    [Fact]
    public void Save_NullSettings_Invalid()
    {
        var request = Save();
        request.Plan.Settings = null!;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Plan settings are required");
    }

    [Fact]
    public void Save_FullSettings_Valid()
    {
        var request = Save();
        var s = request.Plan.Settings;
        s.DayLimitMinutes = TripPace.Relaxed;
        s.StartDate = new DateOnly(2026, 11, 1);
        s.Adults = 2;
        s.Kids = 0;

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_Adults0_Invalid()
    {
        var request = Save();
        request.Plan.Settings.Adults = 0;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Adults must be 1 to 30");
    }

    [Fact]
    public void Save_Adults30_Valid()
    {
        var request = Save();
        request.Plan.Settings.Adults = 30;

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_Adults31_Invalid()
    {
        var request = Save();
        request.Plan.Settings.Adults = 31;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Adults must be 1 to 30");
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public void Save_Kids(int kids, bool valid)
    {
        var request = Save();
        request.Plan.Settings.Kids = kids;

        var result = SaveValidator.Validate(request);
        Assert.Equal(valid, result.IsValid);
        if (!valid) ValidationAssert.HasMessage(result, "Kids must be 0 to 20");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public void Save_MaxDaysInRange_Valid(int days)
    {
        var request = Save();
        request.Plan.Settings.MaxDays = days;

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void Save_MaxDaysOutOfRange_Invalid(int days)
    {
        var request = Save();
        request.Plan.Settings.MaxDays = days;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), PlanTripRequestValidator.MaxDaysMessage);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(14, true)]
    [InlineData(0, false)]
    [InlineData(15, false)]
    public void Plan_MaxDays_Range(int? days, bool ok)
    {
        var request = Auto();
        request.MaxDays = days;

        var result = PlanValidator.Validate(request);
        if (ok) Assert.True(result.IsValid);
        else ValidationAssert.HasMessage(result, PlanTripRequestValidator.MaxDaysMessage);
    }

    // ---- trip steps (CONTRACT_STEPS 2.2) ----

    private static SaveTripRequest WithSteps(TripSteps steps)
    {
        var request = Save();
        request.Plan.Steps = steps;
        return request;
    }

    [Fact]
    public void Save_FullSteps_Valid()
    {
        var request = WithSteps(new TripSteps
        {
            StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 },
            GettingThere = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = Guid.NewGuid(), Date = new DateOnly(2026, 11, 1) },
            ComingBack = new TripStep { Status = TripStepStatuses.Own },
        });

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_EmptyStepsObject_Valid()
        => SaveValidator.TestValidate(WithSteps(new TripSteps())).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_StepBadStatus_Invalid()
    {
        var request = WithSteps(new TripSteps { GettingThere = new TripStep { Status = "asked" } });

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Invalid step status");
    }

    [Fact]
    public void Save_StartFromBadLat_Invalid()
    {
        var request = WithSteps(new TripSteps { StartFrom = new TripStartFrom { Name = "Delhi", Lat = 91, Lon = 77.21 } });

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Invalid coordinates");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Save_StartFromBlankName_FallsBack(string name)
    {
        var start = new TripStartFrom { Name = name, Lat = 28.61, Lon = 77.21 };
        var request = WithSteps(new TripSteps { StartFrom = start });

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
        Assert.Equal("My location", start.Name);
    }

    [Fact]
    public void Save_StartFromName81_Truncated()
    {
        var start = new TripStartFrom { Name = new string('a', 81), Lat = 28.61, Lon = 77.21 };

        SaveValidator.TestValidate(WithSteps(new TripSteps { StartFrom = start })).ShouldNotHaveAnyValidationErrors();
        Assert.Equal(80, start.Name.Length);
    }

    [Fact]
    public void Save_StepDateOutOfRange_Invalid()
    {
        var request = WithSteps(new TripSteps { GettingThere = new TripStep { Date = new DateOnly(1999, 12, 31) } });

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Invalid travel date");
    }

    [Fact]
    public void Save_StepsFromJson_EmptyJourneyIdReadsNull()
    {
        var json = "{\"status\":\"Draft\",\"plan\":{\"version\":6,\"cityKey\":502141,\"settings\":{\"dayLimitMinutes\":450},"
                   + "\"legs\":[{\"index\":1,\"cityKey\":502141,\"cityName\":\"Nainital\",\"latitude\":29.38,\"longitude\":79.45}],"
                   + "\"days\":[{\"day\":1,\"spots\":[]}],"
                   + "\"steps\":{\"gettingThere\":{\"status\":\"notStarted\",\"journeyId\":\"\"}}}}";
        var request = System.Text.Json.JsonSerializer.Deserialize<SaveTripRequest>(json, TripJson.Options)!;

        Assert.Null(request.Plan.Steps!.GettingThere!.JourneyId);
        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    // ---- SaveTripRequestValidator, legs ----

    private const long SecondCity = 210000456;

    private static TripCityLeg Leg(int index, long cityKey, string name = "City") => new()
    {
        Index = index, CityKey = cityKey, CityName = name, Latitude = 30.74, Longitude = 79.49,
    };

    // dayLegs: the leg of each day, e.g. [1, 2, 2].
    private static SaveTripRequest MultiCity(int[] dayLegs, params long[] cityKeys)
    {
        var request = Save(null, Enumerable.Repeat(1, dayLegs.Length).ToArray());
        request.Plan.Legs = cityKeys.Select((k, i) => Leg(i + 1, k)).ToList();
        for (var i = 0; i < dayLegs.Length; i++) request.Plan.Days[i].Leg = dayLegs[i];
        return request;
    }

    [Fact]
    public void Save_TwoLegs_Valid()
        => SaveValidator.TestValidate(MultiCity([1, 2, 2], CityKey, SecondCity)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_FifthLeg_Invalid()
        => ValidationAssert.HasMessage(
            SaveValidator.Validate(MultiCity([1, 2, 3, 4, 5], CityKey, 2, 3, 4, 5)), SaveTripRequestValidator.MaxLegsMessage);

    [Fact]
    public void Save_FourLegs_Valid()
        => SaveValidator.TestValidate(MultiCity([1, 2, 3, 4], CityKey, 2, 3, 4)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_DuplicateCity_Invalid()
        => ValidationAssert.HasMessage(
            SaveValidator.Validate(MultiCity([1, 2], CityKey, CityKey)), SaveTripRequestValidator.DuplicateCityMessage);

    [Fact]
    public void Save_LegWithoutCity_Invalid()
        => ValidationAssert.HasMessage(SaveValidator.Validate(MultiCity([1, 2], CityKey, 0)), "City is required");

    [Fact]
    public void Save_LegGap_Invalid()
        => ValidationAssert.HasMessage(
            SaveValidator.Validate(MultiCity([1, 1, 3], CityKey, SecondCity, 3)), SaveTripRequestValidator.DaysFollowLegsMessage);

    [Fact]
    public void Save_DaysNotStartingAtTheFirstCity_Invalid()
        => ValidationAssert.HasMessage(
            SaveValidator.Validate(MultiCity([2, 2], CityKey, SecondCity)), SaveTripRequestValidator.DaysFollowLegsMessage);

    [Fact]
    public void Save_LastDayNotInTheLastCity_Invalid()
        => ValidationAssert.HasMessage(
            SaveValidator.Validate(MultiCity([1, 1], CityKey, SecondCity)), SaveTripRequestValidator.DaysFollowLegsMessage);

    [Fact]
    public void Save_LegNameMissing_Invalid()
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        request.Plan.Legs![1].CityName = " ";

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "City name must be 1 to 120 characters");
    }

    [Fact]
    public void Save_ArrivalOnTheFirstLeg_Invalid()
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        request.Plan.Legs![0].Arrival = new TripTravel { FromName = "Delhi" };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), SaveTripRequestValidator.FirstArrivalMessage);
    }

    // ---- travel between cities by a saved journey (CONTRACT_MCB D3)

    private static SaveTripRequest WithArrival(TripTravel arrival)
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        request.Plan.Legs![1].Arrival = arrival;
        return request;
    }

    private static TripTravel JourneyTravel(Guid? id = null) => new()
    {
        FromName = "Haridwar", JourneyId = id ?? Guid.NewGuid(),
        PlanKey = null, Cars = 2, ListedTotal = 14000, RoadMinutes = 600,
    };

    [Fact]
    public void Save_JourneyArrival_Valid()
        => SaveValidator.TestValidate(WithArrival(JourneyTravel())).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_JourneyArrivalRoadMinutes600_Valid()
    {
        var travel = JourneyTravel();
        travel.RoadMinutes = 600;

        SaveValidator.TestValidate(WithArrival(travel)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_JourneyArrivalRoadMinutes901_Invalid()
    {
        var travel = JourneyTravel();
        travel.RoadMinutes = 901;

        ValidationAssert.HasMessage(SaveValidator.Validate(WithArrival(travel)), SaveTripRequestValidator.RoadTimeMessage);
    }

    [Fact]
    public void Save_JourneyArrivalWithoutId_Invalid()
    {
        var travel = JourneyTravel();
        travel.JourneyId = null;

        ValidationAssert.HasMessage(SaveValidator.Validate(WithArrival(travel)), SaveTripRequestValidator.TravelMessage);
    }

    [Fact]
    public void Save_JourneyArrivalEmptyId_Invalid()
        => ValidationAssert.HasMessage(
            SaveValidator.Validate(WithArrival(JourneyTravel(Guid.Empty))), SaveTripRequestValidator.TravelMessage);

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Save_JourneyArrivalCarsOutOfRange_Invalid(int cars)
    {
        var travel = JourneyTravel();
        travel.Cars = cars;

        ValidationAssert.HasMessage(SaveValidator.Validate(WithArrival(travel)), SaveTripRequestValidator.TravelMessage);
    }

    [Fact]
    public void Save_JourneyArrivalBadPlanKey_Invalid()
    {
        var travel = JourneyTravel();
        travel.PlanKey = "not a key";

        ValidationAssert.HasMessage(SaveValidator.Validate(WithArrival(travel)), SaveTripRequestValidator.TravelMessage);
    }

    [Fact]
    public void Save_SixtySpots_Valid()
        => SaveValidator.TestValidate(Save(null, Enumerable.Repeat(6, 10).ToArray())).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_NotReadyLegWithOneEmptyDay_Valid()
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        request.Plan.Days[1].Spots.Clear();
        request.Plan.Legs![1].NotReady = true;

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    // ---- plan v6: caps, marks, optional dates and people ----

    [Fact]
    public void Save_VersionSix_Valid()
        => SaveValidator.TestValidate(Save()).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    public void Save_AnyOtherVersion_Invalid(int version)
    {
        var request = Save();
        request.Plan.Version = version;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), SaveTripRequestValidator.VersionMessage);
    }

    [Fact]
    public void Save_WithoutLegs_CityRequired()
    {
        var request = Save();
        request.Plan.Legs = null;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "City is required");

        request.Plan.Legs = [];
        ValidationAssert.HasMessage(SaveValidator.Validate(request), "City is required");
    }

    [Fact]
    public void Save_SixPlacesADay_Valid()
        => SaveValidator.TestValidate(Save(null, 6, 6)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_SevenPlacesOnADay_TooManySpotsPerDay()
    {
        var result = SaveValidator.Validate(Save(null, 3, 7));

        ValidationAssert.HasMessage(result, "Up to 6 places a day");
        Assert.Contains(result.Errors, e => e.ErrorCode == TripErrorTypes.TooManySpotsPerDay);
    }

    [Fact]
    public void Save_NoDatesAndNoPeople_Valid()
    {
        var request = Save(TripStatuses.Planned, 2);
        request.Plan.Settings.StartDate = null;
        request.Plan.Settings.Adults = null;
        request.Plan.Settings.Kids = null;

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_DefaultPaceIsRelaxed()
        => Assert.Equal(TripPace.Relaxed, Save().Plan.Settings.DayLimitMinutes);

    [Theory]
    [InlineData("own", true)]
    [InlineData("skipped", true)]
    [InlineData("done", false)]
    [InlineData("", false)]
    public void Save_TaxiMark_Status(string status, bool valid)
    {
        var request = Save();
        request.Plan.TaxiMark = new TripRowMark { Status = status };

        var result = SaveValidator.Validate(request);
        Assert.Equal(valid, result.IsValid);
        if (!valid) ValidationAssert.HasMessage(result, SaveTripRequestValidator.MarkMessage);
    }

    [Fact]
    public void Save_StayMarkOnAnyLeg_Valid()
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        request.Plan.Legs![0].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
        request.Plan.Legs[1].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_BadStayMark_Invalid()
    {
        var request = Save();
        request.Plan.Legs![0].StayMark = new TripRowMark { Status = "booked" };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), SaveTripRequestValidator.MarkMessage);
    }

    [Fact]
    public void Save_TravelMarkOnLegTwo_Valid()
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        request.Plan.Legs![1].TravelMark = new TripRowMark { Status = TripRowMarkStatuses.Own };

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_TravelMarkOnTheFirstLeg_Invalid()
    {
        var request = Save();
        request.Plan.Legs![0].TravelMark = new TripRowMark { Status = TripRowMarkStatuses.Own };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), SaveTripRequestValidator.TravelMessage);
    }

    [Fact]
    public void Save_TravelMarkWithArrival_Invalid()
    {
        var request = WithArrival(JourneyTravel());
        request.Plan.Legs![1].TravelMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), SaveTripRequestValidator.TravelMessage);
    }

    [Fact]
    public void Save_NotNeededStep_Invalid()
    {
        var request = WithSteps(new TripSteps { ComingBack = new TripStep { Status = "notNeeded" } });

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Invalid step status");
    }

    [Fact]
    public void Save_OwnAndSkippedSteps_Valid()
        => SaveValidator.TestValidate(WithSteps(new TripSteps
        {
            GettingThere = new TripStep { Status = TripStepStatuses.Own },
            ComingBack = new TripStep { Status = TripStepStatuses.Skipped },
        })).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Plan_RecalculateSevenOnADay_TooManySpotsPerDay()
    {
        var result = PlanValidator.Validate(Recalc(3, 7));

        ValidationAssert.HasMessage(result, PlanTripRequestValidator.TooManyPerDayMessage);
        Assert.Contains(result.Errors, e => e.ErrorCode == TripErrorTypes.TooManySpotsPerDay);
    }

    [Fact]
    public void Plan_RecalculateSixOnEveryDay_Valid()
        => PlanValidator.TestValidate(Recalc(6, 6, 6)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Plan_AutoMoreThanDaysTimesSix_TooManySpotsForDays()
    {
        var request = Auto(13);
        request.MaxDays = 2;

        var result = PlanValidator.Validate(request);

        ValidationAssert.HasMessage(result, "Up to 12 places for 2 days (6 a day)");
        Assert.Contains(result.Errors, e => e.ErrorCode == TripErrorTypes.TooManySpotsForDays);
        Assert.Equal("Up to 12 places for 2 days (6 a day)", PlanTripRequestValidator.TooManyForDaysMessage(2));
    }

    [Theory]
    [InlineData(2, 12)]
    [InlineData(10, 60)]
    [InlineData(14, 60)]
    public void Plan_TooManyForDaysMessage_NeverAboveTripCap(int days, int shown)
        => Assert.Equal($"Up to {shown} places for {days} days (6 a day)", PlanTripRequestValidator.TooManyForDaysMessage(days));

    [Theory]
    [InlineData(1, 6, true)]
    [InlineData(1, 7, false)]
    [InlineData(2, 12, true)]
    [InlineData(3, 18, true)]
    [InlineData(3, 19, false)]
    public void Plan_AutoMaxDaysCap(int days, int spots, bool valid)
    {
        var request = Auto(spots);
        request.MaxDays = days;

        Assert.Equal(valid, PlanValidator.Validate(request).IsValid);
    }

    [Fact]
    public void Plan_AutoWithoutMaxDays_OnlyTheTripCapApplies()
        => PlanValidator.TestValidate(Auto(40)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Plan_AutoWithoutStartDate_Valid()
    {
        var request = Auto();
        request.StartDate = null;

        PlanValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }
}
