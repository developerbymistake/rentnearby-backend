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

        RuleFor(x => x.ProposedAts)
            .Must(p => p != null && p.Count > 0).WithMessage("ProposedAts must include at least one time when countering")
            .When(x => x.Action == "counter");

        RuleFor(x => x.AcceptedAt)
            .NotNull().WithMessage("AcceptedAt is required when accepting a proposed time")
            .When(x => x.Action == "accept");
    }
}
