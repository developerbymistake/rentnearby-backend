using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateServiceSectionRequestValidator : AbstractValidator<UpdateServiceSectionRequest>
{
    public UpdateServiceSectionRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name cannot be empty")
            .MaximumLength(100)
            .When(x => x.Name != null);

        RuleFor(x => x.IconName)
            .NotEmpty().WithMessage("IconName cannot be empty")
            .MaximumLength(100)
            .When(x => x.IconName != null);
    }
}
