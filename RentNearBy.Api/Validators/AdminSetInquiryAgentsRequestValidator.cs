using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class AdminSetInquiryAgentsRequestValidator : AbstractValidator<AdminSetInquiryAgentsRequest>
{
    public AdminSetInquiryAgentsRequestValidator()
    {
        // An empty list is a legitimate full-replace ("unassign all agents") — only reject a null
        // list or an unreasonably large one (sanity cap, no real inquiry ever has this many agents).
        RuleFor(x => x.AgentIds)
            .NotNull().WithMessage("AgentIds must be provided (an empty list unassigns all agents)")
            .Must(ids => ids.Count <= 50).WithMessage("Too many agents");
    }
}
