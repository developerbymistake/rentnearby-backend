using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpsertPlanRequestValidator : AbstractValidator<UpsertPlanRequest>
{
    public UpsertPlanRequestValidator()
    {
        RuleFor(x => x.FeatureKey).Must(PlanFeatures.IsValid).WithMessage("FeatureKey must be Room, Plot, Stay or Taxi");
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9_]+$")
            .WithMessage("Code may contain only letters, digits and underscores");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Tagline).MaximumLength(120);
        RuleFor(x => x.TrialDays).InclusiveBetween(0, PlanRules.MaxTrialDays);
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10_000);

        RuleForEach(x => x.Features).ChildRules(f =>
        {
            f.RuleFor(v => v.Key).Must(k => PlanFeatureKeys.All.Contains(k))
                .WithMessage($"Feature key must be one of: {string.Join(", ", PlanFeatureKeys.All)}");
            f.RuleFor(v => v.Value).InclusiveBetween(0, 10_000);
        });
        RuleFor(x => x.Features)
            .Must(fs => fs == null || fs.Select(f => f.Key).Distinct().Count() == fs.Count)
            .WithMessage("Each feature key can appear once");

        RuleForEach(x => x.Prices).ChildRules(p =>
        {
            p.RuleFor(v => v.CycleMonths).Must(PlanCycles.IsValid).WithMessage("CycleMonths must be 1, 3 or 12");
            p.RuleFor(v => v.PriceInr).InclusiveBetween(1, 1_000_000);
            p.RuleFor(v => v.RazorpayPlanId).MaximumLength(64);
        });
        RuleFor(x => x.Prices)
            .Must(ps => ps == null || ps.Select(p => p.CycleMonths).Distinct().Count() == ps.Count)
            .WithMessage("Each billing cycle can appear once");
    }
}
