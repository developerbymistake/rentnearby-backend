using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class CreateCouponRequestValidator : AbstractValidator<CreateCouponRequest>
{
    public CreateCouponRequestValidator()
    {
        // Only manually-typed codes can be admin-created — WELCOME_SIGNUP is system-seeded once, and
        // REFERRAL has no working redemption path yet (see WellKnownCoupons.ReferralTrigger).
        RuleFor(x => x.TriggerType)
            .Equal(WellKnownCoupons.ManualCodeTrigger)
            .WithMessage($"Admin-created coupons must use TriggerType '{WellKnownCoupons.ManualCodeTrigger}'.");
        RuleFor(x => x.CoinValue).GreaterThan(0).WithMessage("CoinValue must be greater than 0");
        RuleFor(x => x.MaxTotalRedemptions).GreaterThan(0).When(x => x.MaxTotalRedemptions.HasValue);
        RuleFor(x => x.ValidUntil)
            .GreaterThan(x => x.ValidFrom)
            .When(x => x.ValidUntil.HasValue)
            .WithMessage("ValidUntil must be after ValidFrom");
    }
}
