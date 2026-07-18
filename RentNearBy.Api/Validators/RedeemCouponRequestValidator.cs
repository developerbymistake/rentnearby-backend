using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class RedeemCouponRequestValidator : AbstractValidator<RedeemCouponRequest>
{
    public RedeemCouponRequestValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code is required")
            .MaximumLength(20).WithMessage("Code is too long");
    }
}
