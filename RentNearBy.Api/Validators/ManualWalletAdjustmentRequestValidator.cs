using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class ManualWalletAdjustmentRequestValidator : AbstractValidator<ManualWalletAdjustmentRequest>
{
    public ManualWalletAdjustmentRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0).WithMessage("Amount must be greater than 0");
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A reason is required").MaximumLength(500);
        RuleFor(x => x.IdempotencyKey).NotEmpty();
    }
}
