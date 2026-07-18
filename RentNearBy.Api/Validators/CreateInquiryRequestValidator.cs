using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateInquiryRequestValidator : AbstractValidator<CreateInquiryRequest>
{
    public CreateInquiryRequestValidator()
    {
        RuleFor(x => x.ServiceId).NotEmpty().WithMessage("ServiceId is required");
        RuleFor(x => x.ServicePackageId).NotEmpty().WithMessage("ServicePackageId is required");

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("FullName is required")
            .MaximumLength(150).WithMessage("FullName must not exceed 150 characters");

        RuleFor(x => x.Mobile)
            .NotEmpty().WithMessage("Mobile is required")
            .Matches(@"^\d{10}$").WithMessage("Mobile must be 10 digits");

        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("Email must be a valid email address")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));

        RuleFor(x => x.NumberOfPeople)
            .GreaterThan(0).WithMessage("NumberOfPeople must be greater than 0")
            .When(x => x.NumberOfPeople.HasValue);

        RuleFor(x => x.Message).MaximumLength(1000).When(x => x.Message != null);

        RuleFor(x => x.AgreedToTerms)
            .Equal(true).WithMessage("You must agree to the terms to submit an inquiry");
    }
}
