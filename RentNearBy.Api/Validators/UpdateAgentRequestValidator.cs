using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateAgentRequestValidator : AbstractValidator<UpdateAgentRequest>
{
    public UpdateAgentRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name cannot be empty")
            .MaximumLength(150)
            .When(x => x.Name != null);

        RuleFor(x => x.Phone)
            .Matches(@"^\d{10}$").WithMessage("Phone must be 10 digits")
            .When(x => x.Phone != null);

        RuleFor(x => x.WhatsAppNumber)
            .Matches(@"^\d{10}$").WithMessage("WhatsAppNumber must be 10 digits")
            .When(x => x.WhatsAppNumber != null);
    }
}
