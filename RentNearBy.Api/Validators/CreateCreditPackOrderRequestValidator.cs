using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateCreditPackOrderRequestValidator : AbstractValidator<CreateCreditPackOrderRequest>
{
    public CreateCreditPackOrderRequestValidator()
    {
        RuleFor(x => x.CreditPackId).NotEmpty().WithMessage("CreditPackId is required");
    }
}
