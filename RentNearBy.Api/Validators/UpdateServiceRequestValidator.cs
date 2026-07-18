using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateServiceRequestValidator : AbstractValidator<UpdateServiceRequest>
{
    public UpdateServiceRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name cannot be empty")
            .MaximumLength(150)
            .When(x => x.Name != null);

        RuleFor(x => x.IconName)
            .NotEmpty().WithMessage("IconName cannot be empty")
            .MaximumLength(100)
            .When(x => x.IconName != null);

        RuleFor(x => x.ShortDescription)
            .NotEmpty().WithMessage("ShortDescription cannot be empty")
            .MaximumLength(300)
            .When(x => x.ShortDescription != null);

        RuleFor(x => x.FullDescription)
            .NotEmpty().WithMessage("FullDescription cannot be empty")
            .When(x => x.FullDescription != null);
    }
}
