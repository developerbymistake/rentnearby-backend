using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    public SendMessageRequestValidator()
    {
        RuleFor(x => x.Type)
            .Must(t => t is "quick_reply" or "contact_request" or "schedule_proposal")
            .WithMessage("Type must be one of: quick_reply, contact_request, schedule_proposal");

        RuleFor(x => x.PayloadJson)
            .NotEmpty()
            .MaximumLength(2000).WithMessage("Payload too large");
    }
}
