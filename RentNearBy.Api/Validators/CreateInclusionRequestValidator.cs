using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateInclusionRequestValidator : AbstractValidator<CreateInclusionRequest>
{
    public CreateInclusionRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(100).WithMessage("Name must not exceed 100 characters");

        RuleFor(x => x.IconName)
            .NotEmpty().WithMessage("IconName is required")
            .MaximumLength(100);
    }
}
