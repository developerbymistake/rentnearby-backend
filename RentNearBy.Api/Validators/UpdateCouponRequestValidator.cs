using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateCouponRequestValidator : AbstractValidator<UpdateCouponRequest>
{
    public UpdateCouponRequestValidator()
    {
        RuleFor(x => new CouponRuleInput(x.Kind, x.Value, x.MaxDiscountInr, x.MinPlanPriceInr, x.FeatureKey,
                x.PerUserLimit, x.MaxTotalRedemptions, x.ValidFrom, x.ValidUntil, x.AutoApply, x.CampaignLabel))
            .SetValidator(new CouponRuleValidator());
    }
}
