using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateListingRequestValidator : AbstractValidator<CreateListingRequest>
{
    public CreateListingRequestValidator()
    {
        RuleFor(x => x.RoomTypeId)
            .NotEmpty().WithMessage("Room type is required");

        RuleFor(x => x.DistrictId)
            .NotEmpty().WithMessage("District is required");

        RuleFor(x => x.CityId)
            .NotEmpty().WithMessage("City is required");

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90, 90).WithMessage("Invalid latitude");

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180, 180).WithMessage("Invalid longitude");

        RuleFor(x => x.PriceMonthly)
            .GreaterThan(0).WithMessage("Monthly rent must be greater than 0")
            .LessThanOrEqualTo(9900000).WithMessage("Monthly rent cannot exceed ₹99,00,000");

        RuleFor(x => x.Description)
            .MaximumLength(300).WithMessage("Description must not exceed 300 characters")
            .When(x => x.Description != null);

        RuleFor(x => x.Address)
            .NotEmpty().WithMessage("Address is required")
            .MaximumLength(500).WithMessage("Address must not exceed 500 characters");

        RuleFor(x => x.FurnishedStatus)
            .Must(v => v == "None" || v == "Semi" || v == "Full")
            .WithMessage("Furnished status must be None, Semi, or Full");
    }
}
