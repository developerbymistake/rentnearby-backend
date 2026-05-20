using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreatePlotRequestValidator : AbstractValidator<CreatePlotRequest>
{
    private static readonly string[] AllowedAreaUnits = ["sqft", "sqm", "bigha", "marla", "acre", "kanal"];

    public CreatePlotRequestValidator()
    {
        RuleFor(x => x.AreaValue)
            .GreaterThan(0).WithMessage("Area must be greater than 0");

        RuleFor(x => x.AreaUnit)
            .NotEmpty().WithMessage("Area unit is required")
            .Must(u => AllowedAreaUnits.Contains(u)).WithMessage("Invalid area unit. Allowed: sqft, sqm, bigha, marla, acre, kanal");

        RuleFor(x => x.PlotTypeId)
            .NotEmpty().WithMessage("Plot type is required");

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90, 90).WithMessage("Invalid latitude");

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180, 180).WithMessage("Invalid longitude");

        RuleFor(x => x.Address)
            .NotEmpty().WithMessage("Address is required")
            .MaximumLength(500).WithMessage("Address must not exceed 500 characters");

        RuleFor(x => x.DistrictId)
            .NotEmpty().WithMessage("District is required");

        RuleFor(x => x.Description)
            .MaximumLength(300).WithMessage("Description must not exceed 300 characters")
            .When(x => x.Description != null);
    }
}
