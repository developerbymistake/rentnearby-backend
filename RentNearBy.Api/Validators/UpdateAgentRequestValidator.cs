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
            .Matches(@"^[6-9]\d{9}$").WithMessage("Phone must be a valid 10-digit Indian mobile number")
            .When(x => x.Phone != null);

        RuleFor(x => x.WhatsAppNumber)
            .Matches(@"^[6-9]\d{9}$").WithMessage("WhatsAppNumber must be a valid 10-digit Indian mobile number")
            .When(x => x.WhatsAppNumber != null);

        RuleFor(x => x.Experience)
            .InclusiveBetween(0, 60).WithMessage("Experience must be between 0 and 60 years")
            .When(x => x.Experience != null);

        RuleFor(x => x.CompanyName)
            .MaximumLength(100).WithMessage("Company name must be 100 characters or fewer")
            .When(x => x.CompanyName != null);
    }
}
