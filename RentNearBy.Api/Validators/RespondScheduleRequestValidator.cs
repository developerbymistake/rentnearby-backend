using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class RespondScheduleRequestValidator : AbstractValidator<RespondScheduleRequest>
{
    public RespondScheduleRequestValidator()
    {
        RuleFor(x => x.Action)
            .Must(a => a is "accept" or "decline" or "counter")
            .WithMessage("Action must be one of: accept, decline, counter");

        RuleFor(x => x.ProposedAt)
            .NotNull().WithMessage("ProposedAt is required when countering with a new time")
            .When(x => x.Action == "counter");
    }
}
