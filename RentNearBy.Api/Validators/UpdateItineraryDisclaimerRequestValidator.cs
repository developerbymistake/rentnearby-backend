using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateItineraryDisclaimerRequestValidator : AbstractValidator<UpdateItineraryDisclaimerRequest>
{
    public UpdateItineraryDisclaimerRequestValidator()
    {
        RuleFor(x => x.HillRegionText).MaximumLength(2000).When(x => x.HillRegionText != null)
            .WithMessage("HillRegionText must not exceed 2000 characters");
        RuleFor(x => x.GeneralText).MaximumLength(2000).When(x => x.GeneralText != null)
            .WithMessage("GeneralText must not exceed 2000 characters");
    }
}
