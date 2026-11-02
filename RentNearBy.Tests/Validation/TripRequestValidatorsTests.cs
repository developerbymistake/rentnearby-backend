using FluentValidation.TestHelper;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Validation;

public class TripRequestValidatorsTests
{
    private static readonly PlanTripRequestValidator PlanValidator = new();
    private static readonly SaveTripRequestValidator SaveValidator = new();
    private static readonly Guid CityId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid DistrictId = Guid.Parse("66666666-7777-8888-9999-000000000000");

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
        CityId = CityId, Mode = TripPlanModes.Auto, Spots = Enumerable.Range(0, spots).Select(Input).ToList(),
    };

    private static PlanTripRequest Recalc(params int[] spotsPerDay) => new()
    {
        CityId = CityId,
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
            CityId = CityId,
            DistrictId = DistrictId,
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
        request.CityId = Guid.Empty;

        PlanValidator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.CityId);
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
    public void Plan_AutoFortySpots_Valid()
        => PlanValidator.TestValidate(Auto(40)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Plan_AutoFortyOneSpots_TooManySpots()
    {
        var result = PlanValidator.Validate(Auto(41));

        ValidationAssert.HasMessage(result, "A trip can have up to 40 places");
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

        ValidationAssert.HasMessage(validator.Validate(Auto(41)), "A trip can have up to 40 places");
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
    public void Plan_RecalculateFortyOneSpots_TooManySpots()
        => ValidationAssert.HasMessage(PlanValidator.Validate(Recalc(30, 11)), "A trip can have up to 40 places");

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
        request.Plan.Version = 2;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Unsupported plan version");
    }

    [Fact]
    public void Save_EmptyCityOrDistrict_Invalid()
    {
        var request = Save();
        request.Plan.CityId = Guid.Empty;
        request.Plan.DistrictId = Guid.Empty;

        var result = SaveValidator.Validate(request);

        ValidationAssert.HasMessage(result, "City is required");
        ValidationAssert.HasMessage(result, "District is required");
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
    public void Save_FortyOneSpots_TooManySpots()
    {
        var result = SaveValidator.Validate(Save(null, 20, 21));

        ValidationAssert.HasMessage(result, "A trip can have up to 40 places");
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

    [Theory]
    [InlineData(0, true)]
    [InlineData(9_900_000, true)]
    [InlineData(-1, false)]
    [InlineData(9_900_001, false)]
    public void Save_TaxiFare(int fare, bool valid)
    {
        var request = Save();
        request.Plan.Days[0].Taxi = new TripTaxiChoice { TaxiId = Guid.NewGuid(), Name = "Ramesh", Fare = fare };

        Assert.Equal(valid, SaveValidator.Validate(request).IsValid);
    }

    [Fact]
    public void Save_TaxiNameTooLong_Invalid()
    {
        var request = Save();
        request.Plan.Days[0].Taxi = new TripTaxiChoice { TaxiId = Guid.NewGuid(), Name = new string('n', 121) };

        Assert.False(SaveValidator.Validate(request).IsValid);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(2500, true)]
    [InlineData(9_900_001, false)]
    public void Save_StayPrice(int price, bool valid)
    {
        var request = Save();
        request.Plan.Days[0].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Lake View", PricePerNight = price };

        Assert.Equal(valid, SaveValidator.Validate(request).IsValid);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, false)]
    public void Save_SameAsDay_MustPointBack(int sameAsDay, bool valid)
    {
        var request = Save(null, 1, 1);
        request.Plan.Days[0].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Lake View", PricePerNight = 1600 };
        request.Plan.Days[1].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Lake View", SameAsDay = sameAsDay };

        Assert.Equal(valid, SaveValidator.Validate(request).IsValid);
    }
}
