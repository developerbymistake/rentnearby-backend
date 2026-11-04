using FluentValidation.TestHelper;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Taxis;

public class TaxiLocalRateValidatorTests
{
    private static readonly UpdateTaxiLocalRateRequestValidator Validator = new();

    [Fact]
    public void DayRate_Null_ClearsValid()
        => Validator.TestValidate(new UpdateTaxiLocalRateRequest()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void DayRate_Below100_Invalid()
        => Validator.TestValidate(new UpdateTaxiLocalRateRequest { DayRate = 99 })
            .ShouldHaveValidationErrorFor(x => x.DayRate)
            .WithErrorMessage(UpsertTaxiRoutesRequestValidator.FareRangeMessage);

    [Fact]
    public void DayRate_AboveMax_Invalid()
        => Validator.TestValidate(new UpdateTaxiLocalRateRequest { DayRate = TaxiLimits.MaxFare + 1 })
            .ShouldHaveValidationErrorFor(x => x.DayRate);

    [Fact]
    public void Km_WithoutRate_Invalid()
        => Validator.TestValidate(new UpdateTaxiLocalRateRequest { KmPerDay = 80 })
            .ShouldHaveValidationErrorFor(x => x.KmPerDay)
            .WithErrorMessage("Add a day rate first");

    [Fact]
    public void Km_1001_Invalid()
        => Validator.TestValidate(new UpdateTaxiLocalRateRequest { DayRate = 1800, KmPerDay = 1001 })
            .ShouldHaveValidationErrorFor(x => x.KmPerDay)
            .WithErrorMessage("Km per day must be 0 to 1000");

    [Fact]
    public void Km_Negative_Invalid()
        => Validator.TestValidate(new UpdateTaxiLocalRateRequest { DayRate = 1800, KmPerDay = -1 })
            .ShouldHaveValidationErrorFor(x => x.KmPerDay);

    [Fact]
    public void Rate1800Km120_Valid()
        => Validator.TestValidate(new UpdateTaxiLocalRateRequest { DayRate = 1800, KmPerDay = 120 })
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void RateOnly_Valid()
        => Validator.TestValidate(new UpdateTaxiLocalRateRequest { DayRate = 1800 })
            .ShouldNotHaveAnyValidationErrors();
}
