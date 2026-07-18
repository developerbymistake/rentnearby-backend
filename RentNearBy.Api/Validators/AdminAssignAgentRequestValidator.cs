using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class AdminAssignAgentRequestValidator : AbstractValidator<AdminAssignAgentRequest>
{
    public AdminAssignAgentRequestValidator()
    {
        // AgentId is nullable by design — null means "unassign", not a validation failure. Only
        // reject an accidental empty-guid, which is never a real Agent id.
        RuleFor(x => x.AgentId)
            .NotEqual(Guid.Empty).WithMessage("AgentId cannot be an empty guid")
            .When(x => x.AgentId.HasValue);
    }
}
