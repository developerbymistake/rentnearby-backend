using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class PhoneOnboardingRequestValidator : AbstractValidator<PhoneOnboardingRequest>
{
    public PhoneOnboardingRequestValidator()
    {
        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage("Phone number is required")
            .Matches(@"^[6-9]\d{9}$").WithMessage("Phone number must be a valid 10-digit Indian mobile number");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(100).WithMessage("Name cannot exceed 100 characters");
    }
}
