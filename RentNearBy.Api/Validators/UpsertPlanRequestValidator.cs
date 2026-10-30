using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpsertPlanRequestValidator : AbstractValidator<UpsertPlanRequest>
{
    public const string PlayProductIdPattern = "^[a-z0-9][a-z0-9_.]{0,39}$";
    public const string PlayBasePlanIdPattern = "^[a-z0-9][a-z0-9-]{0,39}$";

    public UpsertPlanRequestValidator()
    {
        RuleFor(x => x.FeatureKey).Must(PlanFeatures.IsValid).WithMessage("FeatureKey must be Room, Plot, Stay or Taxi");
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9_]+$")
            .WithMessage("Code may contain only letters, digits and underscores");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Tagline).MaximumLength(120);
        RuleFor(x => x.TrialDays).InclusiveBetween(0, PlanRules.MaxTrialDays);
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10_000);
        RuleFor(x => x.PlayProductId).Matches(PlayProductIdPattern)
            .When(x => !string.IsNullOrWhiteSpace(x.PlayProductId))
            .WithMessage("Play product id: lowercase letters, digits, '_' or '.', starting with a letter or digit, max 40");

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
            p.RuleFor(v => v.PlayBasePlanId).Matches(PlayBasePlanIdPattern)
                .When(v => !string.IsNullOrWhiteSpace(v.PlayBasePlanId))
                .WithMessage("Play base plan id: lowercase letters, digits or '-', starting with a letter or digit, max 40");
            p.RuleFor(v => v.PlayTrialOfferId).Matches(PlayBasePlanIdPattern)
                .When(v => !string.IsNullOrWhiteSpace(v.PlayTrialOfferId))
                .WithMessage("Play offer id: lowercase letters, digits or '-', starting with a letter or digit, max 40");
            p.RuleFor(v => v).Must(v => string.IsNullOrWhiteSpace(v.PlayTrialOfferId) || !string.IsNullOrWhiteSpace(v.PlayBasePlanId))
                .WithMessage("A trial offer id needs a base plan id");
            p.RuleFor(v => v).Must(v => string.IsNullOrWhiteSpace(v.PlayTrialOfferId) || v.CycleMonths == 1)
                .WithMessage("The trial offer goes on the 1-month price only");
        });
        RuleFor(x => x.Prices)
            .Must(ps => ps == null || ps.Select(p => p.CycleMonths).Distinct().Count() == ps.Count)
            .WithMessage("Each billing cycle can appear once");
        RuleFor(x => x.Prices)
            .Must(ps => ps == null || ps.Count(p => !string.IsNullOrWhiteSpace(p.PlayTrialOfferId)) <= 1)
            .WithMessage("Only one price can have a trial offer");
        RuleFor(x => x.Prices)
            .Must(ps => ps == null || ps.Where(p => !string.IsNullOrWhiteSpace(p.PlayBasePlanId))
                .GroupBy(p => p.PlayBasePlanId!.Trim()).All(g => g.Count() == 1))
            .WithMessage("Each Play base plan id can appear once");
        RuleFor(x => x).Must(x => x.TrialDays > 0 || (x.Prices ?? []).All(p => string.IsNullOrWhiteSpace(p.PlayTrialOfferId)))
            .WithMessage("A trial offer id needs trial days above 0");
    }
}
