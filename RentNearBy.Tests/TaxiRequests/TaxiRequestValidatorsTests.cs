using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// One case per 400 rule of the traveller bodies. The error code carries the contract error type.
public class TaxiRequestValidatorsTests
{
    private static CreateTaxiRequestDto Valid() => new()
    {
        ClientRequestId = Guid.NewGuid(),
        Pickup = new CreateTaxiPickupDto { On = true, CityKey = 6377270 },
        Sightseeing = new CreateTaxiSightseeingDto { On = true, Days = [1, 2, 4] },
        Drop = new CreateTaxiDropDto { On = true, SameAsPickup = true },
        Seats = 4,
        DayStats = [new TaxiDayStatDto { Day = 1, Km = 25, Minutes = 370 }],
    };

    private static string? Code(CreateTaxiRequestDto body)
    {
        var result = new CreateTaxiRequestValidator().Validate(body);
        return result.IsValid ? null : result.Errors[0].ErrorCode;
    }

    [Fact]
    public void A_valid_create_passes() => Assert.Null(Code(Valid()));

    [Fact]
    public void All_sections_off_is_TaxiSectionsRequired()
    {
        var body = Valid();
        body.Pickup.On = false;
        body.Sightseeing.On = false;
        body.Drop.On = false;
        Assert.Equal(TaxiRequestErrorTypes.TaxiSectionsRequired, Code(body));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(12)]
    public void Seats_other_than_4_6_7_are_refused(int seats)
    {
        var body = Valid();
        body.Seats = seats;
        var result = new CreateTaxiRequestValidator().Validate(body);
        Assert.Equal(TaxiRequestCreateRules.SeatsMessage, Assert.Single(result.Errors).ErrorMessage);
    }

    [Fact]
    public void Empty_sightseeing_days_are_SightseeingDaysInvalid()
    {
        var body = Valid();
        body.Sightseeing.Days = [];
        Assert.Equal(TaxiRequestErrorTypes.SightseeingDaysInvalid, Code(body));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void Sightseeing_day_outside_1_to_14_is_SightseeingDaysInvalid(int day)
    {
        var body = Valid();
        body.Sightseeing.Days = [1, day];
        Assert.Equal(TaxiRequestErrorTypes.SightseeingDaysInvalid, Code(body));
    }

    [Fact]
    public void Repeated_sightseeing_day_is_SightseeingDaysInvalid()
    {
        var body = Valid();
        body.Sightseeing.Days = [2, 2];
        Assert.Equal(TaxiRequestErrorTypes.SightseeingDaysInvalid, Code(body));
    }

    [Fact]
    public void Sightseeing_days_are_not_checked_while_sightseeing_is_off()
    {
        var body = Valid();
        body.Sightseeing = new CreateTaxiSightseeingDto { On = false, Days = [] };
        Assert.Null(Code(body));
    }

    [Theory]
    [InlineData(-1, 10, 1)]
    [InlineData(2001, 10, 1)]
    [InlineData(10, -1, 1)]
    [InlineData(10, 1441, 1)]
    [InlineData(10, 10, 0)]
    [InlineData(10, 10, 15)]
    public void Day_figures_out_of_range_are_refused(int km, int minutes, int day)
    {
        var body = Valid();
        body.DayStats = [new TaxiDayStatDto { Day = day, Km = km, Minutes = minutes }];
        var result = new CreateTaxiRequestValidator().Validate(body);
        Assert.Equal(CreateTaxiRequestValidator.DayStatMessage, Assert.Single(result.Errors).ErrorMessage);
    }

    [Fact]
    public void Repeated_day_figures_are_refused()
    {
        var body = Valid();
        body.DayStats = [new TaxiDayStatDto { Day = 1, Km = 1, Minutes = 1 }, new TaxiDayStatDto { Day = 1, Km = 2, Minutes = 2 }];
        Assert.NotNull(Code(body));
    }

    [Fact]
    public void An_empty_client_request_id_is_refused()
    {
        var body = Valid();
        body.ClientRequestId = Guid.Empty;
        Assert.NotNull(Code(body));
    }

    [Fact]
    public void A_missing_client_request_id_is_allowed()
    {
        var body = Valid();
        body.ClientRequestId = null;
        Assert.Null(Code(body));
    }

    [Theory]
    [InlineData("TravellerCancelled", null, true)]
    [InlineData("PickupChanged", null, true)]
    [InlineData("TravellerCancelled", "ChangedPlan", true)]
    [InlineData("AdminCancelled", null, false)]
    [InlineData("", null, false)]
    [InlineData("TravellerCancelled", "Rude", false)]
    public void Cancel_body_rules(string reason, string? chip, bool valid)
        => Assert.Equal(valid, new CancelTaxiRequestValidator().Validate(new CancelTaxiRequestDto { Reason = reason, Chip = chip }).IsValid);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void Rating_is_1_to_5_stars(int stars, bool valid)
        => Assert.Equal(valid, new RateTaxiRequestValidator().Validate(new RateTaxiRequestDto { Stars = stars }).IsValid);
}
