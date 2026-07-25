using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateCreditPackRequestValidator : AbstractValidator<UpdateCreditPackRequest>
{
    public UpdateCreditPackRequestValidator()
    {
        RuleFor(x => x.Credits).GreaterThan(0).When(x => x.Credits.HasValue);
        RuleFor(x => x.BonusCredits).GreaterThanOrEqualTo(0).When(x => x.BonusCredits.HasValue);
        RuleFor(x => x.PriceInr).GreaterThan(0).When(x => x.PriceInr.HasValue);
    }
}
