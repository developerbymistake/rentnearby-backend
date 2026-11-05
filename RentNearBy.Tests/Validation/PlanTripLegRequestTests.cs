using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Validation;

public class PlanTripLegRequestTests
{
    private static readonly PlanTripRequestValidator Validator = new();

    private static PlanTripRequest Request(string mode = TripPlanModes.Auto)
    {
        var spot = new PlanSpotInput { Id = "osm:N1", Name = "Place 1", Latitude = 29.39, Longitude = 79.45, Category = "attraction", VisitMinutes = 60 };
        return mode == TripPlanModes.Auto
            ? new PlanTripRequest { CityKey = 210000456, Mode = mode, Spots = [spot] }
            : new PlanTripRequest { CityKey = 210000456, Mode = mode, Days = [new PlanDayInput { Day = 1, Spots = [spot] }] };
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(450, true)]
    [InlineData(900, true)]
    [InlineData(-1, false)]
    [InlineData(901, false)]
    public void FirstDayUsedMinutes_Range_BothModes(int? minutes, bool ok)
    {
        foreach (var mode in new[] { TripPlanModes.Auto, TripPlanModes.Recalculate })
        {
            var request = Request(mode);
            request.FirstDayUsedMinutes = minutes;

            var result = Validator.Validate(request);
            if (ok) Assert.True(result.IsValid);
            else ValidationAssert.HasMessage(result, PlanTripRequestValidator.InvalidRoadTimeMessage);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(0, false)]
    [InlineData(5, false)]
    public void LegIndex_Range(int? leg, bool ok)
    {
        var request = Request();
        request.LegIndex = leg;

        var result = Validator.Validate(request);
        if (ok) Assert.True(result.IsValid);
        else ValidationAssert.HasMessage(result, PlanTripRequestValidator.InvalidCityMessage);
    }
}
