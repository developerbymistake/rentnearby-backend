using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateDistrictRequestValidator : AbstractValidator<CreateDistrictRequest>
{
    public CreateDistrictRequestValidator()
    {
        RuleFor(x => x.CityId)
            .NotEmpty().WithMessage("City is required");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("District name is required")
            .MaximumLength(100).WithMessage("District name must not exceed 100 characters");
    }
}
