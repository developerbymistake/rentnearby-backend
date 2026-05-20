using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdatePlotTypeRequestValidator : AbstractValidator<UpdatePlotTypeRequest>
{
    public UpdatePlotTypeRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Plot type name cannot be empty")
            .MaximumLength(100).WithMessage("Name must not exceed 100 characters")
            .When(x => x.Name != null);

        RuleFor(x => x.Description)
            .MaximumLength(300).WithMessage("Description must not exceed 300 characters")
            .When(x => x.Description != null);
    }
}
