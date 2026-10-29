using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpdateTaxiVehicleTypeRequestValidator : AbstractValidator<UpdateTaxiVehicleTypeRequest>
{
    public UpdateTaxiVehicleTypeRequestValidator()
    {
        RuleFor(x => x.Name)
            .Cascade(CascadeMode.Stop)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Name is required")
            .Must(n => n!.Trim().Length is >= 2 and <= TaxiLimits.VehicleTypeNameMax).WithMessage("Name must be 2 to 60 characters")
            .When(x => x.Name != null);

        RuleFor(x => x.Category)
            .Must(c => TaxiVehicleCategories.All.Contains(c!.Trim()))
            .WithMessage("Pick a valid category")
            .When(x => x.Category != null);

        // 0 clears the default.
        RuleFor(x => x.DefaultSeats)
            .Must(s => s == 0 || TaxiSeats.IsValid(s)).WithMessage("Seats must be 4, 6, 7 or 12+")
            .When(x => x.DefaultSeats != null);

        RuleFor(x => x.SortOrder)
            .GreaterThanOrEqualTo(0).WithMessage("Sort order must be 0 or more")
            .When(x => x.SortOrder != null);
    }
}
