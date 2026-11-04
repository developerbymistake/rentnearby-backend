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
    public void Save_Version4_Invalid()
    {
        var request = Save();
        request.Plan.Version = 4;

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
}
