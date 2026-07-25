using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class SetAgentServicesRequestValidator : AbstractValidator<SetAgentServicesRequest>
{
    public SetAgentServicesRequestValidator()
    {
        // An empty list is a legitimate full-replace ("unassign from all services") — only reject a null list.
        RuleFor(x => x.ServiceIds).NotNull().WithMessage("ServiceIds must be provided (an empty list unassigns from all services)");
    }
}
