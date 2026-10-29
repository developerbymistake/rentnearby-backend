using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class RejectGoLiveRequestValidator : AbstractValidator<RejectGoLiveRequest>
{
    public RejectGoLiveRequestValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("A rejection reason is required")
            .MaximumLength(300).WithMessage("Reason must not exceed 300 characters");
    }
}
