using FluentValidation.TestHelper;
using RentNearBy.Api.Validators;

namespace RentNearBy.Tests.Validation;

public class UpdatePaymentFeatureRequestValidatorTests
{
    private static readonly UpdatePaymentFeatureRequestValidator Validator = new();

    [Fact]
    public void Defaults_Valid()
        => Validator.TestValidate(Requests.UpdatePaymentFeature()).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(null, true)]
    public void FreeDurationDays_PositiveWhenSet(int? days, bool valid)
    {
        var result = Validator.TestValidate(Requests.UpdatePaymentFeature() with { FreeDurationDays = days });

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.FreeDurationDays);
        else result.ShouldHaveValidationErrorFor(x => x.FreeDurationDays).WithErrorMessage("FreeDurationDays must be greater than 0");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(101, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(null, true)]
    public void FreeModeListingLimit_Range(int? limit, bool valid)
    {
        var result = Validator.TestValidate(Requests.UpdatePaymentFeature() with { FreeModeListingLimit = limit });

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else result.ShouldHaveValidationErrorFor(x => x.FreeModeListingLimit).WithErrorMessage("FreeModeListingLimit must be between 1 and 100");
    }

    [Fact]
    public void SetAndClearTogether_Error()
        => ValidationAssert.HasMessage(
            Validator.TestValidate(Requests.UpdatePaymentFeature() with { FreeModeListingLimit = 5, ClearFreeModeListingLimit = true }),
            "Cannot both set and clear FreeModeListingLimit");

    [Fact]
    public void ClearOnly_Valid()
        => Validator.TestValidate(Requests.UpdatePaymentFeature() with { ClearFreeModeListingLimit = true }).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("Play", true)]
    [InlineData("Razorpay", true)]
    [InlineData(null, true)]
    [InlineData("Stripe", false)]
    [InlineData("None", false)]
    [InlineData("play", false)]
    public void Provider_Selectable(string? provider, bool valid)
    {
        var result = Validator.TestValidate(Requests.UpdatePaymentFeature() with { Provider = provider });

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else result.ShouldHaveValidationErrorFor(x => x.Provider);
    }
}
