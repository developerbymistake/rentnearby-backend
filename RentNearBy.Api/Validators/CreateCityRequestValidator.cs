using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateCityRequestValidator : AbstractValidator<CreateCityRequest>
{
    public CreateCityRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("City name is required")
            .MaximumLength(100).WithMessage("City name must not exceed 100 characters");
    }
}
