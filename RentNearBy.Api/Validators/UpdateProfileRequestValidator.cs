using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotNull().WithMessage("Name is required.")
            .NotEmpty().WithMessage("Name cannot be blank.")
            .MaximumLength(100).WithMessage("Name cannot exceed 100 characters.");

        RuleFor(x => x.PhoneNumber)
            .Matches(@"^\d{10}$").WithMessage("Phone number must be 10 digits")
            .When(x => x.PhoneNumber != null);
    }
}
