using FluentValidation.TestHelper;
using RentNearBy.Api.Validators;

namespace RentNearBy.Tests.Validation;

public class UpsertTaxiBasicRequestValidatorTests
{
    private static readonly UpsertTaxiBasicRequestValidator Validator = new();

    // The validator reads today's date, so dates are built relative to it.
    private static string YearsAgo(int years, int extraDays = 0)
        => TaxiIdentityRules.FormatDob(TaxiIdentityRules.TodayIst().AddYears(-years).AddDays(extraDays));

    [Fact]
    public void Defaults_Valid()
        => Validator.TestValidate(Requests.UpsertTaxiBasic()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void IdentityFieldsNull_Valid()
    {
        var request = Requests.UpsertTaxiBasic();
        request.DrivingLicenceNumber = null;
        request.DateOfBirth = null;

        Validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("uk-07-2015-0012345", true)]
    [InlineData("XX0720150012345", false)]
    [InlineData("UK0020150012345", false)]
    [InlineData("UK07 15 0012345", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void DrivingLicenceNumber_WhenSent(string value, bool valid)
    {
        var request = Requests.UpsertTaxiBasic();
        request.DrivingLicenceNumber = value;

        var result = Validator.TestValidate(request);

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.DrivingLicenceNumber);
        else result.ShouldHaveValidationErrorFor(x => x.DrivingLicenceNumber).WithErrorMessage(TaxiIdentityRules.DlInvalidMessage);
    }

    [Fact]
    public void DrivingLicenceNumber_NextYear_Invalid()
    {
        var request = Requests.UpsertTaxiBasic();
        request.DrivingLicenceNumber = $"UK07{TaxiIdentityRules.TodayIst().Year + 1}0012345";

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.DrivingLicenceNumber);
    }

    [Theory]
    [InlineData("14/05/1990")]
    [InlineData("1990-5-14")]
    [InlineData("1990-02-30")]
    [InlineData("")]
    public void DateOfBirth_BadFormat_Invalid(string value)
    {
        var request = Requests.UpsertTaxiBasic();
        request.DateOfBirth = value;

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.DateOfBirth)
            .WithErrorMessage(TaxiIdentityRules.DobInvalidMessage);
    }

    [Fact]
    public void DateOfBirth_ExactlyMinAge_Valid()
    {
        var request = Requests.UpsertTaxiBasic();
        request.DateOfBirth = YearsAgo(TaxiIdentityRules.MinAgeYears);

        Validator.TestValidate(request).ShouldNotHaveValidationErrorFor(x => x.DateOfBirth);
    }

    [Fact]
    public void DateOfBirth_OneDayUnderMinAge_Minor()
    {
        var request = Requests.UpsertTaxiBasic();
        request.DateOfBirth = YearsAgo(TaxiIdentityRules.MinAgeYears, extraDays: 1);

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.DateOfBirth)
            .WithErrorMessage(TaxiIdentityRules.DobMinorMessage);
    }

    [Fact]
    public void DateOfBirth_Tomorrow_Invalid()
    {
        var request = Requests.UpsertTaxiBasic();
        request.DateOfBirth = YearsAgo(0, extraDays: 1);

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.DateOfBirth)
            .WithErrorMessage(TaxiIdentityRules.DobInvalidMessage);
    }

    [Fact]
    public void DateOfBirth_OverMaxAge_Invalid()
    {
        var request = Requests.UpsertTaxiBasic();
        request.DateOfBirth = YearsAgo(TaxiIdentityRules.MaxAgeYears + 1);

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.DateOfBirth)
            .WithErrorMessage(TaxiIdentityRules.DobInvalidMessage);
    }
}
