using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdatePlotRequestValidator : AbstractValidator<UpdatePlotRequest>
{
    private static readonly string[] AllowedAreaUnits = ["sqft", "bigha", "acre", "nali"];

    public UpdatePlotRequestValidator()
    {
        RuleFor(x => x.AreaValue)
            .GreaterThan(0).WithMessage("Area must be greater than 0")
            .When(x => x.AreaValue.HasValue);

        RuleFor(x => x.AreaUnit)
            .Must(u => AllowedAreaUnits.Contains(u!)).WithMessage("Invalid area unit. Allowed: sqft, bigha, acre, nali")
            .When(x => x.AreaUnit != null);

        RuleFor(x => x.PlotTypeId)
            .NotEmpty().WithMessage("Plot type ID cannot be empty")
            .When(x => x.PlotTypeId.HasValue);

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90, 90).WithMessage("Invalid latitude")
            .When(x => x.Latitude.HasValue);

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180, 180).WithMessage("Invalid longitude")
            .When(x => x.Longitude.HasValue);

        RuleFor(x => x.Address)
            .MaximumLength(500).WithMessage("Address must not exceed 500 characters")
            .When(x => x.Address != null);

        RuleFor(x => x.Description)
            .MaximumLength(300).WithMessage("Description must not exceed 300 characters")
            .When(x => x.Description != null);
    }
}
