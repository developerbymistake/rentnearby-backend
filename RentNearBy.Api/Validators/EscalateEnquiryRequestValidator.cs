using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class EscalateEnquiryRequestValidator : AbstractValidator<EscalateEnquiryRequest>
{
    public EscalateEnquiryRequestValidator()
    {
        RuleFor(x => x.Reason)
            .Must(r => EscalationReasons.All.Contains(r))
            .WithMessage("Invalid reason");

        RuleFor(x => x.Note)
            .MaximumLength(500).WithMessage("Note must not exceed 500 characters")
            .When(x => x.Note != null);
    }
}
