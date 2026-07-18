using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateCoinPackRequestValidator : AbstractValidator<UpdateCoinPackRequest>
{
    public UpdateCoinPackRequestValidator()
    {
        RuleFor(x => x.Coins).GreaterThan(0).When(x => x.Coins.HasValue);
        RuleFor(x => x.BonusCoins).GreaterThanOrEqualTo(0).When(x => x.BonusCoins.HasValue);
        RuleFor(x => x.PriceInr).GreaterThan(0).When(x => x.PriceInr.HasValue);
    }
}
