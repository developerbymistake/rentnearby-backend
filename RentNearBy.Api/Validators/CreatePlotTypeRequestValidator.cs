using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreatePlotTypeRequestValidator : AbstractValidator<CreatePlotTypeRequest>
{
    public CreatePlotTypeRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("PlotListing type name is required")
            .MaximumLength(100).WithMessage("Name must not exceed 100 characters");

        RuleFor(x => x.Description)
            .MaximumLength(300).WithMessage("Description must not exceed 300 characters")
            .When(x => x.Description != null);
    }
}
