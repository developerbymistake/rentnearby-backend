using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class RejectRoleChangeRequestValidator : AbstractValidator<RejectRoleChangeRequest>
{
    public RejectRoleChangeRequestValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("A rejection reason is required.")
            .MaximumLength(300).WithMessage("Reason must be 300 characters or fewer.");
    }
}
