using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateCoinPackRequestValidator : AbstractValidator<CreateCoinPackRequest>
{
    public CreateCoinPackRequestValidator()
    {
        RuleFor(x => x.Coins).GreaterThan(0).WithMessage("Coins must be greater than 0");
        RuleFor(x => x.BonusCoins).GreaterThanOrEqualTo(0);
        RuleFor(x => x.PriceInr).GreaterThan(0).WithMessage("PriceInr must be greater than 0");
    }
}
