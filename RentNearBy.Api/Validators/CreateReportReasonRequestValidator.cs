using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateReportReasonRequestValidator : AbstractValidator<CreateReportReasonRequest>
{
    public CreateReportReasonRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Reason name is required")
            .MaximumLength(100).WithMessage("Name must not exceed 100 characters");

        RuleFor(x => x.Description)
            .MaximumLength(300).WithMessage("Description must not exceed 300 characters")
            .When(x => x.Description != null);
    }
}
