using FluentValidation.TestHelper;
using RentNearBy.Api.Validators;

namespace RentNearBy.Tests.Validation;

public class UpsertStayBasicRequestValidatorTests
{
    private static readonly UpsertStayBasicRequestValidator Validator = new();

    [Fact]
    public void Defaults_Valid()
        => Validator.TestValidate(Requests.UpsertStayBasic()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void RegistrationTypeId_Missing_Error()
    {
        var request = Requests.UpsertStayBasic();
        request.RegistrationTypeId = null;

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.RegistrationTypeId)
            .WithErrorMessage(StayRegistrationRules.TypeRequiredMessage);
    }

    [Fact]
    public void NumberAndGstinNull_KeepStored_Valid()
    {
        var request = Requests.UpsertStayBasic();
        request.RegistrationNumber = null;
        request.Gstin = null;

        Validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("UK/HS/2021/0457", true)]
    [InlineData("hs 2021 0457", true)]
    [InlineData("UDYAM UK 05 0001234", true)]
    [InlineData("A1", false)]
    [InlineData("UK#2021", false)]
    [InlineData("", false)]
    public void RegistrationNumber_Shape(string value, bool valid)
    {
        var request = Requests.UpsertStayBasic();
        request.RegistrationNumber = value;

        var result = Validator.TestValidate(request);

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.RegistrationNumber);
        else result.ShouldHaveValidationErrorFor(x => x.RegistrationNumber)
            .WithErrorMessage(StayRegistrationRules.RegistrationInvalidMessage);
    }

    [Theory]
    [InlineData("", true)]                    // clears
    [InlineData("05abcde1234f1z6", true)]
    [InlineData("29ABCDE1234F1Z5", false)]
    [InlineData("05ABCDE1234F1Z", false)]
    public void Gstin_EmptyClearsElseValidated(string value, bool valid)
    {
        var request = Requests.UpsertStayBasic();
        request.Gstin = value;

        var result = Validator.TestValidate(request);

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.Gstin);
        else result.ShouldHaveValidationErrorFor(x => x.Gstin).WithErrorMessage(StayRegistrationRules.GstinInvalidMessage);
    }

    [Theory]
    [InlineData("Gram Panchayat NOC", true)]
    [InlineData("AB", false)]
    [InlineData("Licence #5", false)]
    public void RegistrationOtherName_WhenSent(string value, bool valid)
    {
        var request = Requests.UpsertStayBasic();
        request.RegistrationOtherName = value;

        var result = Validator.TestValidate(request);

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.RegistrationOtherName);
        else result.ShouldHaveValidationErrorFor(x => x.RegistrationOtherName)
            .WithErrorMessage(StayRegistrationRules.OtherNameInvalidMessage);
    }
}
