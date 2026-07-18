using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpdateCouponRequestValidator : AbstractValidator<UpdateCouponRequest>
{
    public UpdateCouponRequestValidator()
    {
        RuleFor(x => x.CoinValue).GreaterThan(0).When(x => x.CoinValue.HasValue);
        RuleFor(x => x.MaxTotalRedemptions).GreaterThan(0).When(x => x.MaxTotalRedemptions.HasValue);
        RuleFor(x => x.Status)
            .Must(s => s == CouponStatuses.Active || s == CouponStatuses.Revoked)
            .When(x => x.Status != null)
            .WithMessage("Status can only be manually set to Active or Revoked — Exhausted/Expired are system-managed.");
    }
}
