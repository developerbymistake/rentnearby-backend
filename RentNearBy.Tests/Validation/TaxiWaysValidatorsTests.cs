using FluentValidation.TestHelper;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Validation;

// SaveJourneyRequest trip fields (CONTRACT_MCB D2): tripId and legIndex travel together, legIndex is a city leg 2..4.
public class TaxiWaysValidatorsTests
{
    private static readonly SaveJourneyRequestValidator Validator = new();

    private static SaveJourneyRequest Journey(Guid? tripId = null, int? legIndex = null) => new()
    {
        FromName = "Haridwar", FromLat = 29.9457, FromLng = 78.1642,
        ToName = "Badrinath", ToLat = 30.7433, ToLng = 79.4938,
        Date = JourneyRules.TodayIst(DateTime.UtcNow).AddDays(10),
        People = 4,
        PlanKey = "p~haridwar~badrinath",
        Legs =
        [
            new SaveJourneyLegRequest
            {
                LegIndex = 1, LegKey = "l~haridwar~badrinath", FromName = "Haridwar", FromLat = 29.9457, FromLng = 78.1642,
                ToName = "Badrinath", ToLat = 30.7433, ToLng = 79.4938, DriverTaxiId = Guid.NewGuid(), ListedFare = 9800,
            },
        ],
        TripId = tripId,
        LegIndex = legIndex,
    };

    [Fact]
    public void Save_WithoutTrip_Valid()
        => Validator.TestValidate(Journey()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_LegIndexWithoutTrip_Invalid()
        => ValidationAssert.HasMessage(Validator.Validate(Journey(null, 2)), SaveJourneyRequestValidator.PickCityMessage);

    [Fact]
    public void Save_TripWithoutLegIndex_Invalid()
        => ValidationAssert.HasMessage(Validator.Validate(Journey(Guid.NewGuid(), null)), SaveJourneyRequestValidator.PickCityMessage);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void Save_TripLegIndexOutOfRange_Invalid(int legIndex)
        => ValidationAssert.HasMessage(
            Validator.Validate(Journey(Guid.NewGuid(), legIndex)), SaveJourneyRequestValidator.UnknownCityMessage);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Save_TripLegIndex_Valid(int legIndex)
        => Validator.TestValidate(Journey(Guid.NewGuid(), legIndex)).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Save_EmptyTripId_Invalid()
        => ValidationAssert.HasMessage(Validator.Validate(Journey(Guid.Empty, 2)), "Invalid trip");

    [Fact]
    public void Save_BlankNamesAndZeroFare_Valid()
    {
        var request = Journey();
        request.FromName = "";
        request.ToName = "  ";
        request.Legs[0].FromName = "";
        request.Legs[0].ToName = "";
        request.Legs[0].ListedFare = 0;
        Validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Save_TooLongName_Invalid()
    {
        var request = Journey();
        request.FromName = new string('a', JourneyRules.NameMax + 1);
        ValidationAssert.HasMessage(Validator.Validate(request), "Place name is up to 80 characters");
    }

    [Theory]
    [InlineData("m:29.946:78.164", true, false, "My location")]
    [InlineData("v:29.946:78.164", true, false, "Pickup point")]
    [InlineData("v:29.946:78.164", false, true, "Drop point")]
    [InlineData("a110000001", false, false, "Stop")]
    public void Labels_FallBackByKey(string key, bool start, bool end, string expected)
        => Assert.Equal(expected, TaxiWaysLabels.Of("  ", key, start, end));

    [Fact]
    public void Labels_KeepTypedName()
        => Assert.Equal("Home", TaxiWaysLabels.Of(" Home ", "v:1:2", true, false));
}
