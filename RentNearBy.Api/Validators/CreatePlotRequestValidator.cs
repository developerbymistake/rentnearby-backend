using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreatePlotListingRequestValidator : AbstractValidator<CreatePlotListingRequest>
{
    private static readonly string[] AllowedAreaUnits = ["sqft", "bigha", "acre", "nali"];

    public CreatePlotListingRequestValidator()
    {
        RuleFor(x => x.AreaValue)
            .GreaterThan(0).WithMessage("Area must be greater than 0");

        RuleFor(x => x.AreaUnit)
            .NotEmpty().WithMessage("Area unit is required")
            .Must(u => AllowedAreaUnits.Contains(u.ToLower())).WithMessage("Area unit must be one of: sqft, bigha, acre, nali");

        RuleFor(x => x.PlotTypeId)
            .NotEmpty().WithMessage("PlotListing type is required");

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90, 90).WithMessage("Invalid latitude");

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180, 180).WithMessage("Invalid longitude");

        RuleFor(x => x.Address)
            .NotEmpty().WithMessage("Address is required")
            .MaximumLength(300).WithMessage("Address must not exceed 300 characters");

        RuleFor(x => x.DistrictId)
            .NotEmpty().WithMessage("District is required");

        RuleFor(x => x.Description)
            .MaximumLength(300).WithMessage("Description must not exceed 300 characters")
            .When(x => x.Description != null);
    }
}
