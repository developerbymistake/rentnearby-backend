using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateListingRequestValidator : AbstractValidator<CreateListingRequest>
{
    public CreateListingRequestValidator()
    {
        RuleFor(x => x.RoomTypeId)
            .NotEmpty().WithMessage("Room type is required");

        RuleFor(x => x.CityId)
            .NotEmpty().WithMessage("City is required");

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90, 90).WithMessage("Invalid latitude");

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180, 180).WithMessage("Invalid longitude");

        RuleFor(x => x.PriceMonthly)
            .GreaterThan(0).When(x => x.PriceMonthly.HasValue).WithMessage("Monthly price must be greater than 0");

        RuleFor(x => x.PricePerDay)
            .GreaterThan(0).When(x => x.PricePerDay.HasValue).WithMessage("Per day price must be greater than 0");
    }
}
