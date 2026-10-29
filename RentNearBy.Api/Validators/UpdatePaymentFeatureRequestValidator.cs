using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdatePaymentFeatureRequestValidator : AbstractValidator<UpdatePaymentFeatureRequest>
{
    public UpdatePaymentFeatureRequestValidator()
    {
        RuleFor(x => x.FreeDurationDays).GreaterThan(0).When(x => x.FreeDurationDays.HasValue)
            .WithMessage("FreeDurationDays must be greater than 0");
        RuleFor(x => x.FreeModeListingLimit).InclusiveBetween(1, 100).When(x => x.FreeModeListingLimit.HasValue)
            .WithMessage("FreeModeListingLimit must be between 1 and 100");
        RuleFor(x => x).Must(x => !(x.ClearFreeModeListingLimit && x.FreeModeListingLimit.HasValue))
            .WithMessage("Cannot both set and clear FreeModeListingLimit");
    }
}
