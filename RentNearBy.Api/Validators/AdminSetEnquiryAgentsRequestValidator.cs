using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class AdminSetEnquiryAgentsRequestValidator : AbstractValidator<AdminSetEnquiryAgentsRequest>
{
    public AdminSetEnquiryAgentsRequestValidator()
    {
        // An empty list is a legitimate full-replace ("unassign all agents") — only reject a null
        // list or an unreasonably large one (sanity cap, no real enquiry ever has this many agents).
        RuleFor(x => x.AgentIds)
            .NotNull().WithMessage("AgentIds must be provided (an empty list unassigns all agents)")
            .Must(ids => ids.Count <= 50).WithMessage("Too many agents");
    }
}
