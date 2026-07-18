using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateCoinPackOrderRequestValidator : AbstractValidator<CreateCoinPackOrderRequest>
{
    public CreateCoinPackOrderRequestValidator()
    {
        RuleFor(x => x.CoinPackId).NotEmpty().WithMessage("CoinPackId is required");
    }
}
