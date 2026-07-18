using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateServiceRequestValidator : AbstractValidator<CreateServiceRequest>
{
    public CreateServiceRequestValidator()
    {
        RuleFor(x => x.ServiceCategoryId).NotEmpty().WithMessage("ServiceCategoryId is required");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(150).WithMessage("Name must not exceed 150 characters");

        RuleFor(x => x.IconName)
            .NotEmpty().WithMessage("IconName is required")
            .MaximumLength(100);

        RuleFor(x => x.ShortDescription)
            .NotEmpty().WithMessage("ShortDescription is required")
            .MaximumLength(300).WithMessage("ShortDescription must not exceed 300 characters");

        RuleFor(x => x.FullDescription)
            .NotEmpty().WithMessage("FullDescription is required");
    }
}
