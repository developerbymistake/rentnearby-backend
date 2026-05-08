using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name cannot be empty")
            .MaximumLength(100).WithMessage("Name cannot exceed 100 characters")
            .When(x => x.Name != null);

        RuleFor(x => x.GmailId)
            .EmailAddress().WithMessage("Invalid email address")
            .MaximumLength(200).WithMessage("Email cannot exceed 200 characters")
            .When(x => x.GmailId != null);
    }
}
