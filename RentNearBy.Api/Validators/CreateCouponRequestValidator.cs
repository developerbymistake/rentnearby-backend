using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class CreateCouponRequestValidator : AbstractValidator<CreateCouponRequest>
{
    public CreateCouponRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches($"^[A-Za-z0-9_-]{{3,{PlanRules.MaxCouponCodeLength}}}$")
            .WithMessage($"Code must be 3 to {PlanRules.MaxCouponCodeLength} letters, digits, - or _");
        RuleFor(x => new CouponRuleInput(x.Kind, x.Value, x.MaxDiscountInr, x.MinPlanPriceInr, x.FeatureKey,
                x.PerUserLimit, x.MaxTotalRedemptions, x.ValidFrom, x.ValidUntil, x.AutoApply, x.CampaignLabel))
            .SetValidator(new CouponRuleValidator());
    }
}

public sealed record CouponRuleInput(
    string Kind, int Value, int? MaxDiscountInr, int? MinPlanPriceInr, string? FeatureKey, int PerUserLimit,
    int? MaxTotalRedemptions, DateTime? ValidFrom, DateTime? ValidUntil, bool AutoApply, string? CampaignLabel);

// Rules shared by create and update.
public class CouponRuleValidator : AbstractValidator<CouponRuleInput>
{
    public CouponRuleValidator()
    {
        RuleFor(x => x.Kind).Must(k => CouponKinds.All.Contains(k)).WithMessage("Kind must be Percent, Flat or FreePlan");
        RuleFor(x => x.Value).InclusiveBetween(1, 100).When(x => x.Kind == CouponKinds.Percent)
            .WithMessage("A percent coupon needs a value from 1 to 100");
        RuleFor(x => x.Value).InclusiveBetween(1, 1_000_000).When(x => x.Kind == CouponKinds.Flat)
            .WithMessage("A flat coupon needs a value of at least 1");
        RuleFor(x => x.MaxDiscountInr).GreaterThan(0).When(x => x.MaxDiscountInr.HasValue);
        RuleFor(x => x.MinPlanPriceInr).GreaterThan(0).When(x => x.MinPlanPriceInr.HasValue);
        RuleFor(x => x.FeatureKey).Must(PlanFeatures.IsValid).When(x => x.FeatureKey != null)
            .WithMessage("FeatureKey must be Room, Plot, Stay or Taxi");
        // One live redemption per user per coupon is enforced by a unique index.
        RuleFor(x => x.PerUserLimit).Equal(1).WithMessage("PerUserLimit must be 1");
        RuleFor(x => x.MaxTotalRedemptions).GreaterThan(0).When(x => x.MaxTotalRedemptions.HasValue);
        RuleFor(x => x.ValidUntil).GreaterThan(x => x.ValidFrom)
            .When(x => x.ValidUntil.HasValue && x.ValidFrom.HasValue)
            .WithMessage("ValidUntil must be after ValidFrom");
        RuleFor(x => x.AutoApply).Equal(false).When(x => x.Kind == CouponKinds.FreePlan)
            .WithMessage("A free-plan coupon cannot be auto-applied");
        RuleFor(x => x.CampaignLabel).MaximumLength(80);
    }
}
