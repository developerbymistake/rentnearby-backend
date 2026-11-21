using FluentValidation;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Api.Validators.Owner;

// Lane BE2 request shapes. The percent, nights and start-date lines of SpecialPriceInvalid come from StaySpecialPriceRules in the
// service (they need today and the stored row); these validators only refuse a malformed body.
public sealed class CreateSpecialPriceRequestValidator : AbstractValidator<CreateSpecialPriceRequest>
{
    public CreateSpecialPriceRequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty().WithMessage("Please try again.");
        RuleFor(x => x.Direction).Must(StaySpecialPriceRules.IsDirection).WithMessage("Pick a lower or a higher price.");
    }
}

public sealed class UpdateSpecialPriceRequestValidator : AbstractValidator<UpdateSpecialPriceRequest>
{
    public UpdateSpecialPriceRequestValidator()
    {
        RuleFor(x => x.Direction).Must(StaySpecialPriceRules.IsDirection).WithMessage("Pick a lower or a higher price.");
        RuleFor(x => x.Version).GreaterThan(0).WithMessage("This changed on another phone. Pull to refresh.");
    }
}

public sealed class PauseSpecialPriceRequestValidator : AbstractValidator<PauseSpecialPriceRequest>
{
    public PauseSpecialPriceRequestValidator()
    {
        RuleFor(x => x.Version).GreaterThan(0).WithMessage("This changed on another phone. Pull to refresh.");
    }
}

public sealed class LiveSpecialPriceRequestValidator : AbstractValidator<LiveSpecialPriceRequest>
{
    public LiveSpecialPriceRequestValidator()
    {
        RuleFor(x => x.Version).GreaterThan(0).WithMessage("This changed on another phone. Pull to refresh.");
    }
}

// A2: name 1..40, to >= from, at most 30 days, state at most 100 characters (checked against Districts.StateName in the service).
public sealed class AdminFestivalRequestValidator : AbstractValidator<AdminFestivalRequest>
{
    public AdminFestivalRequestValidator()
    {
        RuleFor(x => x.Name).Must(n => !string.IsNullOrWhiteSpace(n) && n.Trim().Length <= OwnerLimits.FestivalNameMax)
            .WithMessage($"Name must be 1 to {OwnerLimits.FestivalNameMax} characters.");
        RuleFor(x => x).Must(x => x.DateTo >= x.DateFrom).WithMessage("End date cannot be before the start date.");
        RuleFor(x => x).Must(x => x.DateTo.DayNumber - x.DateFrom.DayNumber + 1 <= OwnerLimits.FestivalMaxDays)
            .WithMessage($"A festival can be at most {OwnerLimits.FestivalMaxDays} days.");
        RuleFor(x => x.StateName).Must(s => s == null || s.Trim().Length <= OwnerLimits.StateNameMax).WithMessage("State name is too long.");
        RuleFor(x => x.DateFrom).Must(d => d.Year is >= 2000 and <= 2100).WithMessage("Pick a date between 2000 and 2100.");
    }
}
