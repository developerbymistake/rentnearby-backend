using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateServiceCategoryRequestValidator : AbstractValidator<UpdateServiceCategoryRequest>
{
    public UpdateServiceCategoryRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name cannot be empty")
            .MaximumLength(100)
            .When(x => x.Name != null);

        RuleFor(x => x.IconName)
            .NotEmpty().WithMessage("IconName cannot be empty")
            .MaximumLength(100)
            .When(x => x.IconName != null);

        RuleFor(x => x.FormType)
            .Must(f => f is "Travel" or "Event" or "Consultation" or "Education")
            .WithMessage("FormType must be one of: Travel, Event, Consultation, Education")
            .When(x => x.FormType != null);

        RuleFor(x => x.AgentRoleLabel)
            .NotEmpty().WithMessage("AgentRoleLabel cannot be empty")
            .MaximumLength(50).WithMessage("AgentRoleLabel must not exceed 50 characters")
            .When(x => x.AgentRoleLabel != null);
    }
}
