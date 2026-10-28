using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateListingRequestValidator : AbstractValidator<UpdateListingRequest>
{
    public UpdateListingRequestValidator()
    {
        RuleFor(x => x.PriceMonthly)
            .GreaterThan(0).WithMessage("Monthly rent must be greater than 0")
            .LessThanOrEqualTo(9900000).WithMessage("Monthly rent cannot exceed ₹99,00,000")
            .When(x => x.PriceMonthly.HasValue);

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90, 90).WithMessage("Invalid latitude")
            .When(x => x.Latitude.HasValue);

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180, 180).WithMessage("Invalid longitude")
            .When(x => x.Longitude.HasValue);

        RuleFor(x => x.Description)
            .MaximumLength(300).WithMessage("Description must not exceed 300 characters")
            .When(x => x.Description != null);

        RuleFor(x => x.Address)
            .MaximumLength(500).WithMessage("Address must not exceed 500 characters")
            .When(x => x.Address != null);

        RuleFor(x => x.FurnishedStatus)
            .Must(v => v == "None" || v == "Semi" || v == "Full")
            .WithMessage("Furnished status must be None, Semi, or Full")
            .When(x => x.FurnishedStatus != null);

        RuleFor(x => x.SecurityDeposit)
            .InclusiveBetween(0, 9900000).WithMessage("Security deposit must be between ₹0 and ₹99,00,000")
            .When(x => x.SecurityDeposit.HasValue);

        RuleFor(x => x.Floor)
            .InclusiveBetween((short)0, (short)3).WithMessage("Floor must be Ground, 1, 2 or 3+")
            .When(x => x.Floor.HasValue);
    }
}
