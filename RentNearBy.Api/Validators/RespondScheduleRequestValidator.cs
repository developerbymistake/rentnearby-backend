using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using System.Linq;

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
            .Must(p => p == null || p.All(IsInFuture)).WithMessage("ProposedAts must all be in the future")
            .When(x => x.Action == "counter");

        RuleFor(x => x.AcceptedAt)
            .NotNull().WithMessage("AcceptedAt is required when accepting a proposed time")
            .Must(a => a == null || IsInFuture(a.Value)).WithMessage("AcceptedAt must be in the future")
            .When(x => x.Action == "accept");
    }

    // A small grace window tolerates network latency between client submit and server receipt —
    // a strict > UtcNow could reject a legitimate slightly-delayed request for a slot that was
    // genuinely in the future when the user tapped Send.
    private static bool IsInFuture(DateTime dt) => dt.ToUniversalTime() > DateTime.UtcNow.AddMinutes(-1);
}
