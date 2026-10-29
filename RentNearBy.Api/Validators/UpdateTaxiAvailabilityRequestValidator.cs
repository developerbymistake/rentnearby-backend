using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateTaxiAvailabilityRequestValidator : AbstractValidator<UpdateTaxiAvailabilityRequest>
{
    public UpdateTaxiAvailabilityRequestValidator()
    {
        RuleFor(x => x.IsAvailable)
            .NotNull().WithMessage("Choose Available now on or off");
    }
}
