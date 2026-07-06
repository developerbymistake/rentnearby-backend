using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateListingReportRequestValidator : AbstractValidator<CreateListingReportRequest>
{
    public CreateListingReportRequestValidator()
    {
        RuleFor(x => x.ReasonId)
            .NotEmpty().WithMessage("A reason is required");

        RuleFor(x => x.Details)
            .NotEmpty().WithMessage("Please describe the issue")
            .MaximumLength(500).WithMessage("Details must not exceed 500 characters");
    }
}
