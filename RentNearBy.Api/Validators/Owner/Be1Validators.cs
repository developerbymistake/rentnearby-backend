using FluentValidation;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Api.Validators.Owner;

// Shape only (lane BE1). The calendar rules with their exact copy (labels, nights, phone guard, money, windows) live in the pure
// OwnerCalendarRules / BlockedRangeRules, applied by the services.
public sealed class StayCalendarEntryRequestValidator : AbstractValidator<StayCalendarEntryRequest>
{
    public StayCalendarEntryRequestValidator()
    {
        RuleFor(x => x.DateFrom).NotNull().WithMessage("Pick the check-in date.");
        RuleFor(x => x.DateTo).NotNull().WithMessage("Pick the check-out date.");
        RuleFor(x => x.Label).MaximumLength(500).WithMessage(OwnerCalendarRules.LabelTooLong);
        RuleFor(x => x.Note).MaximumLength(2000).WithMessage(OwnerCalendarRules.NoteTooLong);
    }
}

public sealed class TaxiCalendarEntryRequestValidator : AbstractValidator<TaxiCalendarEntryRequest>
{
    public TaxiCalendarEntryRequestValidator()
    {
        RuleFor(x => x.DateFrom).NotNull().WithMessage("Pick the trip date.");
        RuleFor(x => x.Label).MaximumLength(500).WithMessage(OwnerCalendarRules.LabelTooLong);
        RuleFor(x => x.Note).MaximumLength(2000).WithMessage(OwnerCalendarRules.NoteTooLong);
        RuleFor(x => x.FromPlace).MaximumLength(500).WithMessage(OwnerCalendarRules.PlaceTooLong);
        RuleFor(x => x.ToPlace).MaximumLength(500).WithMessage(OwnerCalendarRules.PlaceTooLong);
        RuleFor(x => x.TimeOfDay).MaximumLength(8).WithMessage("Pick a valid time.");
    }
}

public sealed class StayBlockedRangeRequestValidator : AbstractValidator<StayBlockedRangeRequest>
{
    public StayBlockedRangeRequestValidator()
    {
        RuleFor(x => x.DateFrom).NotNull().WithMessage("Pick the first night.");
        RuleFor(x => x.DateTo).NotNull().WithMessage("Pick the last night.");
        RuleFor(x => x.Reason).MaximumLength(500).WithMessage(BlockedRangeRules.ReasonTooLong);
    }
}
