using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateServiceCategoryRequestValidator : AbstractValidator<CreateServiceCategoryRequest>
{
    public CreateServiceCategoryRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(100).WithMessage("Name must not exceed 100 characters");

        RuleFor(x => x.IconName)
            .NotEmpty().WithMessage("IconName is required")
            .MaximumLength(100);

        RuleFor(x => x.FormType)
            .Must(f => f is "Travel" or "Event" or "Consultation" or "Education")
            .WithMessage("FormType must be one of: Travel, Event, Consultation, Education");

        RuleFor(x => x.AgentRoleLabel)
            .NotEmpty().WithMessage("AgentRoleLabel cannot be empty")
            .MaximumLength(50).WithMessage("AgentRoleLabel must not exceed 50 characters");
    }
}
