using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdatePaymentFeatureRequestValidator : AbstractValidator<UpdatePaymentFeatureRequest>
{
    public UpdatePaymentFeatureRequestValidator()
    {
        RuleFor(x => x.FreeDurationDays).GreaterThan(0).When(x => x.FreeDurationDays.HasValue)
            .WithMessage("FreeDurationDays must be greater than 0");
    }
}
