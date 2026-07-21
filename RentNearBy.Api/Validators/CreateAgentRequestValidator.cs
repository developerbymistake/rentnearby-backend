using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateAgentRequestValidator : AbstractValidator<CreateAgentRequest>
{
    public CreateAgentRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(150).WithMessage("Name must not exceed 150 characters");

        RuleFor(x => x.Phone)
            .NotEmpty().WithMessage("Phone is required")
            .Matches(@"^\d{10}$").WithMessage("Phone must be 10 digits");

        RuleFor(x => x.WhatsAppNumber)
            .NotEmpty().WithMessage("WhatsAppNumber is required")
            .Matches(@"^\d{10}$").WithMessage("WhatsAppNumber must be 10 digits");

        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("UserId is required — link this agent to an existing app account");

        RuleFor(x => x.Experience)
            .InclusiveBetween(0, 60).WithMessage("Experience must be between 0 and 60 years")
            .When(x => x.Experience != null);
    }
}
