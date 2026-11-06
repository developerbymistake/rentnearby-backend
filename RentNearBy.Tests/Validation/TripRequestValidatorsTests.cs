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
        => ValidationAssert.HasMessage(PlanValidator.Validate(Recalc(30, 31)), "A trip can have up to 60 places");

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
        request.Plan.CityKey = 0;

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
        var result = SaveValidator.Validate(Save(null, 30, 31));

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

    // ---- SaveTripRequestValidator, plan v3 ----

    private static TripTaxiBooking Booking(params int[] days) => new()
    {
        AgreedFare = 3800, TollExtra = true, PickupTime = "08:30", PickupPlace = "Mallital", Days = days,
    };

    private static SaveTripRequest Booked(int dayCount, int[] bookingDays, params int[] onDays)
    {
        var request = Save(null, Enumerable.Repeat(1, dayCount).ToArray());
        var taxiId = Guid.NewGuid();
        foreach (var d in onDays)
            request.Plan.Days[d - 1].Taxi = new TripTaxiChoice { TaxiId = taxiId, Name = "Ramesh", Fare = 1900, Booking = Booking(bookingDays) };
        return request;
    }

    [Fact]
    public void Save_Version2_Valid()
    {
        var request = Save();
        request.Plan.Version = 2;

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_Version3_Valid()
    {
        var request = Save();
        request.Plan.Version = 3;

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_Version5_Valid()
    {
        var request = Save();
        request.Plan.Version = 5;

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_Version6_Invalid()
    {
        var request = Save();
        request.Plan.Version = 6;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Unsupported plan version");
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
        s.Rooms = 1;
        s.Needs = [TripNeeds.NoStairs, TripNeeds.KidsSafe, TripNeeds.VegFood];
        s.PickupTime = "23:59";
        s.PickupPlace = "Mallital";

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
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    public void Save_Rooms(int rooms, bool valid)
    {
        var request = Save();
        request.Plan.Settings.Rooms = rooms;

        var result = SaveValidator.Validate(request);
        Assert.Equal(valid, result.IsValid);
        if (!valid) ValidationAssert.HasMessage(result, "Rooms must be 1 to 15");
    }

    [Fact]
    public void Save_NeedsUnknownKey_Invalid()
    {
        var request = Save();
        request.Plan.Settings.Needs = ["pets"];

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Invalid needs");
    }

    [Fact]
    public void Save_NeedsDuplicate_Invalid()
    {
        var request = Save();
        request.Plan.Settings.Needs = [TripNeeds.VegFood, TripNeeds.VegFood];

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Invalid needs");
    }

    [Fact]
    public void Save_NeedsEmpty_Valid()
    {
        var request = Save();
        request.Plan.Settings.Needs = [];

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("12:61")]
    [InlineData("8:30")]
    [InlineData("08:30 am")]
    [InlineData("")]
    public void Save_PickupTime_Invalid(string time)
    {
        var request = Save();
        request.Plan.Settings.PickupTime = time;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Invalid pickup time");
    }

    [Fact]
    public void Save_PickupTime_25_61_Invalid()
    {
        var request = Booked(1, [1], 1);
        request.Plan.Days[0].Taxi!.Booking!.PickupTime = "25:61";

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Invalid pickup time");
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void Save_PickupPlaceBlank_Invalid(string place)
    {
        var request = Save();
        request.Plan.Settings.PickupPlace = place;

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Pickup place can be up to 80 characters");
    }

    [Fact]
    public void Save_PickupPlaceTooLong_Invalid()
    {
        var request = Booked(1, [1], 1);
        request.Plan.Days[0].Taxi!.Booking!.PickupPlace = new string('p', 81);

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Pickup place can be up to 80 characters");
    }

    [Fact]
    public void Save_BookingTwoDays_Valid()
        => SaveValidator.TestValidate(Booked(3, [1, 2], 1, 2)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_BookingDaysOutsidePlan_Invalid()
        => ValidationAssert.HasMessage(SaveValidator.Validate(Booked(2, [1, 3], 1)), "Invalid booking days");

    [Fact]
    public void Save_BookingDaysMissingOwnDay_Invalid()
        => ValidationAssert.HasMessage(SaveValidator.Validate(Booked(2, [1], 2)), "Invalid booking days");

    [Fact]
    public void Save_BookingDaysEmpty_Invalid()
        => ValidationAssert.HasMessage(SaveValidator.Validate(Booked(1, [], 1)), "Invalid booking days");

    [Fact]
    public void Save_BookingDaysDuplicate_Invalid()
        => ValidationAssert.HasMessage(SaveValidator.Validate(Booked(2, [1, 1], 1)), "Invalid booking days");

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(9_900_000, true)]
    [InlineData(9_900_001, false)]
    public void Save_BookingAgreedFare(int fare, bool valid)
    {
        var request = Booked(1, [1], 1);
        request.Plan.Days[0].Taxi!.Booking!.AgreedFare = fare;

        var result = SaveValidator.Validate(request);
        Assert.Equal(valid, result.IsValid);
        if (!valid) ValidationAssert.HasMessage(result, "Invalid fare");
    }

    [Fact]
    public void Save_BookingNoteTooLong_Invalid()
    {
        var request = Booked(1, [1], 1);
        request.Plan.Days[0].Taxi!.Booking!.Note = new string('n', 301);

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Note can be up to 300 characters");
    }

    [Fact]
    public void Save_TaxiWithoutId_Invalid()
    {
        var request = Save();
        request.Plan.Steps = new TripSteps();
        request.Plan.Days[0].Taxi = new TripTaxiChoice { TaxiId = null, Name = "Ramesh" };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Taxi is required");
    }

    [Fact]
    public void Save_TaxiEmptyId_Invalid()
    {
        var request = Save();
        request.Plan.Days[0].Taxi = new TripTaxiChoice { TaxiId = Guid.Empty, Name = "Ramesh" };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Taxi is required");
    }

    [Fact]
    public void Save_OwnTaxiWithoutId_Valid()
    {
        var request = Save();
        request.Plan.Days[0].Taxi = new TripTaxiChoice { IsOwn = true, Name = "My driver", OwnPhone = "9876543210", Note = "Booked by phone" };

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("5876543210")]
    [InlineData("987654321")]
    [InlineData("+919876543210")]
    public void Save_OwnPhone_Invalid(string phone)
    {
        var request = Save();
        request.Plan.Days[0].Stay = new TripStayChoice { IsOwn = true, Name = "My stay", OwnPhone = phone };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Enter a 10 digit phone");
    }

    [Fact]
    public void Save_StayWithoutId_Invalid()
    {
        var request = Save();
        request.Plan.Steps = new TripSteps();
        request.Plan.Days[0].Stay = new TripStayChoice { StayId = null, Name = "Lake View" };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Stay is required");
    }

    [Fact]
    public void Save_StayCoordinates_Valid()
    {
        var request = Save();
        request.Plan.Days[0].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Lake View", Latitude = 29.38, Longitude = 79.46 };

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_StayLatWithoutLon_Invalid()
    {
        var request = Save();
        request.Plan.Days[0].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Lake View", Latitude = 29.38 };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), PlanTripRequestValidator.InvalidCoordinatesMessage);
    }

    [Fact]
    public void Save_StayLatOutOfRange_Invalid()
    {
        var request = Save();
        request.Plan.Days[0].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Lake View", Latitude = 95, Longitude = 79.46 };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), PlanTripRequestValidator.InvalidCoordinatesMessage);
    }

    [Fact]
    public void Save_StayNoteTooLong_Invalid()
    {
        var request = Save();
        request.Plan.Days[0].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Lake View", Note = new string('n', 301) };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Note can be up to 300 characters");
    }

    [Fact]
    public void Save_ThreeShortlisted_Valid()
    {
        var request = Save();
        request.Plan.Days[0].StayShortlist = Enumerable.Range(0, 3)
            .Select(i => new TripStayShortlistItem { StayId = Guid.NewGuid(), Name = $"Stay {i}", PricePerNight = 2000, Latitude = 29.38, Longitude = 79.46, DistanceKm = 1.4 })
            .ToList();
        request.Plan.Days[0].TaxiShortlist = Enumerable.Range(0, 3)
            .Select(i => new TripTaxiShortlistItem { TaxiId = Guid.NewGuid(), Name = $"Driver {i}", Seats = 6, LocalDayRate = 2400, WholeTrip = true })
            .ToList();

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_FourShortlisted_Invalid()
    {
        var request = Save();
        request.Plan.Days[0].StayShortlist = Enumerable.Range(0, 4)
            .Select(i => new TripStayShortlistItem { StayId = Guid.NewGuid(), Name = $"Stay {i}" })
            .ToList();

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Up to 3 shortlisted per day");
    }

    [Fact]
    public void Save_FourTaxisShortlisted_Invalid()
    {
        var request = Save();
        request.Plan.Days[0].TaxiShortlist = Enumerable.Range(0, 4)
            .Select(i => new TripTaxiShortlistItem { TaxiId = Guid.NewGuid(), Name = $"Driver {i}" })
            .ToList();

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Up to 3 shortlisted per day");
    }

    [Fact]
    public void Save_ShortlistSeatsUnknown_Invalid()
    {
        var request = Save();
        request.Plan.Days[0].TaxiShortlist = [new TripTaxiShortlistItem { TaxiId = Guid.NewGuid(), Name = "Driver", Seats = 5 }];

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Invalid seats");
    }

    [Fact]
    public void Save_ShortlistSameAsDayForward_Invalid()
    {
        var request = Save(null, 1, 1);
        request.Plan.Days[0].StayShortlist = [new TripStayShortlistItem { StayId = Guid.NewGuid(), Name = "Stay", SameAsDay = 1 }];

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Same stay must point to an earlier day");
    }

    // ---- own stay / own taxi / no stay tonight / day count (D5, D6, D17) ----

    private static SaveTripRequest FromJson(string dayJson)
    {
        var json = "{\"status\":\"Planned\",\"plan\":{\"version\":3,\"cityKey\":502141,\"settings\":{\"dayLimitMinutes\":450},"
                   + "\"days\":[{\"day\":1,\"spots\":[{\"id\":\"osm:N1\",\"name\":\"Mall Road\",\"latitude\":29.39,\"longitude\":79.45,"
                   + "\"category\":\"attraction\",\"visitMinutes\":60}]," + dayJson + "}]}}";
        return System.Text.Json.JsonSerializer.Deserialize<SaveTripRequest>(json, TripJson.Options)!;
    }

    [Fact]
    public void Save_OwnStayWithEmptyStringId_ReadsAsNull_Valid()
    {
        var request = FromJson("\"stay\":{\"stayId\":\"\",\"name\":\"Hotel Alka\",\"isOwn\":true}");

        Assert.Null(request.Plan.Days[0].Stay!.StayId);
        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_OwnStayAndTaxiWithoutIdKey_Valid()
    {
        var request = FromJson("\"stay\":{\"name\":\"Hotel Alka\",\"isOwn\":true,\"ownPhone\":\"9876543210\"},"
                               + "\"taxi\":{\"taxiId\":\" \",\"name\":\"Mohan\",\"isOwn\":true,\"fare\":1800}");

        Assert.Null(request.Plan.Days[0].Taxi!.TaxiId);
        Assert.Equal(1800, request.Plan.Days[0].Taxi!.Fare);
        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_NonOwnStayWithEmptyStringId_Invalid()
    {
        var request = FromJson("\"stay\":{\"stayId\":\"\",\"name\":\"Lake View\"}");
        request.Plan.Steps = new TripSteps();

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Stay is required");
    }

    [Fact]
    public void Save_NonOwnTaxiWithoutId_Invalid()
    {
        var request = FromJson("\"taxi\":{\"name\":\"Ramesh\"}");
        request.Plan.Steps = new TripSteps();

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Taxi is required");
    }

    [Fact]
    public void Save_NotAGuidId_FailsToRead()
        => Assert.Throws<System.Text.Json.JsonException>(() => FromJson("\"stay\":{\"stayId\":\"abc\",\"name\":\"Lake View\"}"));

    [Fact]
    public void Save_OwnStayNameMissing_Valid()
    {
        var request = Save();
        request.Plan.Days[0].Stay = new TripStayChoice { IsOwn = true, Name = "  " };

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_OwnTaxiName81_Invalid()
    {
        var request = Save();
        request.Plan.Days[0].Taxi = new TripTaxiChoice { IsOwn = true, Name = new string('a', 81) };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Name can be up to 80 characters");
    }

    [Fact]
    public void Save_SkippedNightWithoutId_Valid()
    {
        var request = FromJson("\"stay\":{\"name\":\"No stay tonight\",\"skipped\":true}");

        Assert.True(request.Plan.Days[0].Stay!.Skipped);
        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_SkippedAndOwn_Invalid()
    {
        var request = Save();
        request.Plan.Days[0].Stay = new TripStayChoice { IsOwn = true, Skipped = true, Name = "My stay" };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "A night is either your own stay or no stay");
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
            ComingBack = new TripStep { Status = TripStepStatuses.Own, Note = "Own car, leaving 6 am" },
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
    public void Save_StepNote301_Invalid()
    {
        var request = WithSteps(new TripSteps { ComingBack = new TripStep { Status = TripStepStatuses.Own, Note = new string('a', 301) } });

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Note can be up to 300 characters");
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
    public void Save_StartFromBlankName_Invalid(string name)
    {
        var request = WithSteps(new TripSteps { StartFrom = new TripStartFrom { Name = name, Lat = 28.61, Lon = 77.21 } });

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Starting from must be 1 to 80 characters");
    }

    [Fact]
    public void Save_StartFromName81_Invalid()
    {
        var request = WithSteps(new TripSteps { StartFrom = new TripStartFrom { Name = new string('a', 81), Lat = 28.61, Lon = 77.21 } });

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Starting from must be 1 to 80 characters");
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
        var json = "{\"status\":\"Draft\",\"plan\":{\"version\":3,\"cityKey\":502141,\"settings\":{\"dayLimitMinutes\":450},"
                   + "\"days\":[{\"day\":1,\"spots\":[]}],"
                   + "\"steps\":{\"gettingThere\":{\"status\":\"notStarted\",\"journeyId\":\"\"}}}}";
        var request = System.Text.Json.JsonSerializer.Deserialize<SaveTripRequest>(json, TripJson.Options)!;

        Assert.Null(request.Plan.Steps!.GettingThere!.JourneyId);
        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_SkippedTaxiDayWithoutId_Valid()
    {
        var request = FromJson("\"taxi\":{\"name\":\"No taxi this day\",\"skipped\":true}");

        Assert.True(request.Plan.Days[0].Taxi!.Skipped);
        Assert.Null(request.Plan.Days[0].Taxi!.TaxiId);
        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_PlainTaxiWithoutId_StillInvalid()
    {
        var request = Save();
        request.Plan.Steps = new TripSteps();
        request.Plan.Days[0].Taxi = new TripTaxiChoice { Name = "Ramesh", Skipped = false };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Taxi is required");
    }

    // ---- steps-unaware app writing back a "No taxi this day" day (build 57)

    [Fact]
    public void Save_OldAppTaxiEmptyStringId_WithoutSteps_Valid()
    {
        var request = FromJson("\"taxi\":{\"taxiId\":\"\",\"name\":\"No taxi this day\",\"isOwn\":false}");

        Assert.Null(request.Plan.Steps);
        Assert.Null(request.Plan.Days[0].Taxi!.TaxiId);
        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_OldAppTaxiEmptyStringId_WithSteps_Invalid()
    {
        var request = FromJson("\"taxi\":{\"taxiId\":\"\",\"name\":\"No taxi this day\",\"isOwn\":false}");
        request.Plan.Steps = new TripSteps();

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Taxi is required");
    }

    [Fact]
    public void Save_SkippedTaxiEmptyStringId_WithSteps_Valid()
    {
        var request = FromJson("\"taxi\":{\"taxiId\":\"\",\"name\":\"No taxi this day\",\"skipped\":true}");
        request.Plan.Steps = new TripSteps();

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void KeepSkippedTaxiDays_StoredSkippedDay_StaysSkipped()
    {
        var stored = Save(null, 1, 1).Plan;
        stored.Days[1].Taxi = new TripTaxiChoice { Name = "No taxi this day", Skipped = true };
        var plan = Save(null, 1, 1).Plan;
        plan.Days[0].Taxi = new TripTaxiChoice { Name = "Old", TaxiId = null };
        plan.Days[1].Taxi = new TripTaxiChoice { Name = "No taxi this day", TaxiId = null };

        RentNearBy.Api.Handlers.TripHandlers.KeepSkippedTaxiDays(plan, stored);

        Assert.Null(plan.Days[0].Taxi);
        Assert.True(plan.Days[1].Taxi!.Skipped);
    }

    [Fact]
    public void KeepSkippedTaxiDays_StepsAwareRequest_Untouched()
    {
        var plan = Save().Plan;
        plan.Steps = new TripSteps();
        plan.Days[0].Taxi = new TripTaxiChoice { Name = "Ramesh", TaxiId = null };

        RentNearBy.Api.Handlers.TripHandlers.KeepSkippedTaxiDays(plan, null);

        Assert.NotNull(plan.Days[0].Taxi);
    }

    [Fact]
    public void KeepSkippedTaxiDays_OwnAndPickedTaxis_Untouched()
    {
        var plan = Save(null, 1, 1).Plan;
        var taxiId = Guid.NewGuid();
        plan.Days[0].Taxi = new TripTaxiChoice { Name = "Mohan", IsOwn = true };
        plan.Days[1].Taxi = new TripTaxiChoice { Name = "Ramesh", TaxiId = taxiId };

        RentNearBy.Api.Handlers.TripHandlers.KeepSkippedTaxiDays(plan, null);

        Assert.True(plan.Days[0].Taxi!.IsOwn);
        Assert.Equal(taxiId, plan.Days[1].Taxi!.TaxiId);
        Assert.Null(plan.Days[1].Taxi!.Skipped);
    }

    [Fact]
    public void Save_OldAppStayEmptyStringId_WithoutSteps_Valid()
    {
        var request = FromJson("\"stay\":{\"stayId\":\"\",\"name\":\"No stay tonight\",\"isOwn\":false}");

        Assert.Null(request.Plan.Steps);
        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void KeepSkippedNights_StoredSkippedNight_StaysSkipped()
    {
        var stored = Save(null, 1, 1).Plan;
        stored.Days[1].Stay = new TripStayChoice { Name = "No stay tonight", Skipped = true };
        var plan = Save(null, 1, 1).Plan;
        plan.Days[0].Stay = new TripStayChoice { Name = "Old", StayId = null };
        plan.Days[1].Stay = new TripStayChoice { Name = "No stay tonight", StayId = null };

        RentNearBy.Api.Handlers.TripHandlers.KeepSkippedNights(plan, stored);

        Assert.Null(plan.Days[0].Stay);
        Assert.True(plan.Days[1].Stay!.Skipped);
    }

    [Fact]
    public void KeepSkippedNights_StepsAwareOwnAndPicked_Untouched()
    {
        var plan = Save(null, 1, 1).Plan;
        var stayId = Guid.NewGuid();
        plan.Days[0].Stay = new TripStayChoice { Name = "Hotel Alka", IsOwn = true };
        plan.Days[1].Stay = new TripStayChoice { Name = "Lake View", StayId = stayId };

        RentNearBy.Api.Handlers.TripHandlers.KeepSkippedNights(plan, null);

        Assert.True(plan.Days[0].Stay!.IsOwn);
        Assert.Equal(stayId, plan.Days[1].Stay!.StayId);

        var aware = Save().Plan;
        aware.Steps = new TripSteps();
        aware.Days[0].Stay = new TripStayChoice { Name = "Lake View", StayId = null };
        RentNearBy.Api.Handlers.TripHandlers.KeepSkippedNights(aware, null);
        Assert.NotNull(aware.Days[0].Stay);
    }

    [Fact]
    public void Save_SkippedAndOwnTaxi_Invalid()
    {
        var request = Save();
        request.Plan.Days[0].Taxi = new TripTaxiChoice { IsOwn = true, Skipped = true, Name = "Mohan" };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "A day is either your own taxi or no taxi");
    }

    // ---- SaveTripRequestValidator, plan v4 legs ----

    private const long SecondCity = 210000456;

    private static TripCityLeg Leg(int index, long cityKey, string name = "City") => new()
    {
        Index = index, CityKey = cityKey, CityName = name, Latitude = 30.74, Longitude = 79.49,
    };

    // dayLegs: the leg of each day, e.g. [1, 2, 2].
    private static SaveTripRequest MultiCity(int[] dayLegs, params long[] cityKeys)
    {
        var request = Save(null, Enumerable.Repeat(1, dayLegs.Length).ToArray());
        request.Plan.Version = 4;
        request.Plan.Legs = cityKeys.Select((k, i) => Leg(i + 1, k)).ToList();
        for (var i = 0; i < dayLegs.Length; i++) request.Plan.Days[i].Leg = dayLegs[i];
        return request;
    }

    [Fact]
    public void Save_TwoLegs_Valid()
        => SaveValidator.TestValidate(MultiCity([1, 2, 2], CityKey, SecondCity)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_LeglessBody_PassesAfterNormalise()
    {
        var request = Save(null, 1, 1);
        request.Plan.Version = 3;

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
        var leg = Assert.Single(request.Plan.Legs!);
        Assert.Equal(CityKey, leg.CityKey);
        Assert.All(request.Plan.Days, d => Assert.Equal((int?)1, d.Leg));
    }

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

    [Fact]
    public void Save_OwnArrivalOnLegTwo_Valid()
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        request.Plan.Legs![1].Arrival = new TripTravel
        {
            FromName = "Haridwar", How = TripTravelHows.Train, OwnName = "Shatabdi 12017", OwnPhone = "9876543210",
            LeavesAt = "06:45", RoadMinutes = TripTravelRoad.HalfDay, Note = "Window seats",
        };

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("journey", null, null, SaveTripRequestValidator.TravelMessage)]
    [InlineData("own", "plane", null, SaveTripRequestValidator.TravelMessage)]
    [InlineData("own", "bus", 901, SaveTripRequestValidator.RoadTimeMessage)]
    [InlineData("own", "bus", -1, SaveTripRequestValidator.RoadTimeMessage)]
    [InlineData("train", null, null, SaveTripRequestValidator.TravelMessage)]
    public void Save_BadArrival_Invalid(string mode, string? how, int? roadMinutes, string message)
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        request.Plan.Legs![1].Arrival = new TripTravel { Mode = mode, FromName = "Haridwar", How = how, RoadMinutes = roadMinutes };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), message);
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
        Mode = TripTravelModes.Journey, FromName = "Haridwar", JourneyId = id ?? Guid.NewGuid(),
        PlanKey = null, Cars = 2, ListedTotal = 14000, RoadMinutes = 600,
    };

    [Fact]
    public void TravelModes_HasOwnAndJourney()
    {
        Assert.Equal(2, TripTravelModes.All.Length);
        Assert.Contains(TripTravelModes.Own, TripTravelModes.All);
        Assert.Contains(TripTravelModes.Journey, TripTravelModes.All);
    }

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
    [InlineData("how")]
    [InlineData("ownName")]
    [InlineData("ownPhone")]
    [InlineData("leavesAt")]
    public void Save_JourneyArrivalWithOwnFields_Invalid(string field)
    {
        var travel = JourneyTravel();
        switch (field)
        {
            case "how": travel.How = TripTravelHows.Train; break;
            case "ownName": travel.OwnName = "Shatabdi"; break;
            case "ownPhone": travel.OwnPhone = "9876543210"; break;
            default: travel.LeavesAt = "06:45"; break;
        }

        ValidationAssert.HasMessage(SaveValidator.Validate(WithArrival(travel)), SaveTripRequestValidator.TravelMessage);
    }

    [Fact]
    public void Save_OwnArrivalWithJourneyId_Invalid()
        => ValidationAssert.HasMessage(
            SaveValidator.Validate(WithArrival(new TripTravel
            {
                Mode = TripTravelModes.Own, FromName = "Haridwar", How = TripTravelHows.Train, JourneyId = Guid.NewGuid(),
            })),
            SaveTripRequestValidator.TravelMessage);

    [Fact]
    public void Save_OwnArrivalWithHub_Valid()
        => SaveValidator.TestValidate(WithArrival(new TripTravel
            {
                Mode = TripTravelModes.Own, FromName = "Haridwar", How = TripTravelHows.Train, HubName = "Rishikesh",
            }))
            .ShouldNotHaveAnyValidationErrors();

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
    public void Save_BadArrivalTime_Invalid()
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        request.Plan.Legs![1].Arrival = new TripTravel { FromName = "Haridwar", LeavesAt = "25:00" };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), SaveTripRequestValidator.TimeMessage);
    }

    [Fact]
    public void Save_SameAsDayAcrossLegs_Invalid()
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        request.Plan.Days[0].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Ganga View" };
        request.Plan.Days[1].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Ganga View", SameAsDay = 1 };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), SaveTripRequestValidator.SameCityStayMessage);
    }

    [Fact]
    public void Save_SameAsDayInsideOneLeg_Valid()
    {
        var request = MultiCity([1, 2, 2], CityKey, SecondCity);
        request.Plan.Days[1].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Alka" };
        request.Plan.Days[2].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Alka", SameAsDay = 2 };

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_ShortlistSameAsDayAcrossLegs_Invalid()
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        request.Plan.Days[1].StayShortlist = [new TripStayShortlistItem { StayId = Guid.NewGuid(), Name = "Ganga View", SameAsDay = 1 }];

        ValidationAssert.HasMessage(SaveValidator.Validate(request), SaveTripRequestValidator.SameCityStayMessage);
    }

    [Fact]
    public void Save_ShortlistSameAsDayInsideOneLeg_Valid()
    {
        var request = MultiCity([1, 2, 2], CityKey, SecondCity);
        request.Plan.Days[2].StayShortlist = [new TripStayShortlistItem { StayId = Guid.NewGuid(), Name = "Alka", SameAsDay = 2 }];

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_BookingDaysAcrossLegs_Invalid()
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        var taxiId = Guid.NewGuid();
        foreach (var day in request.Plan.Days)
            day.Taxi = new TripTaxiChoice { TaxiId = taxiId, Name = "Ramesh", Fare = 1900, Booking = Booking(1, 2) };

        ValidationAssert.HasMessage(SaveValidator.Validate(request), "Invalid booking days");
    }

    [Fact]
    public void Save_BookingDaysInsideOneLeg_Valid()
    {
        var request = MultiCity([1, 2, 2], CityKey, SecondCity);
        var taxiId = Guid.NewGuid();
        foreach (var day in request.Plan.Days.Skip(1))
            day.Taxi = new TripTaxiChoice { TaxiId = taxiId, Name = "Ramesh", Fare = 1900, Booking = Booking(2, 3) };

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_SixtySpots_Valid()
        => SaveValidator.TestValidate(Save(null, 30, 30)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_NotReadyLegWithOneEmptyDay_Valid()
    {
        var request = MultiCity([1, 2], CityKey, SecondCity);
        request.Plan.Days[1].Spots.Clear();
        request.Plan.Legs![1].NotReady = true;

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_Version4_Valid()
    {
        var request = Save();
        request.Plan.Version = 4;

        SaveValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }
}
