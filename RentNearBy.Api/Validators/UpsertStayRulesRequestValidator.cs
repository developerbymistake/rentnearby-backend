using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpsertStayRulesRequestValidator : AbstractValidator<UpsertStayRulesRequest>
{
    private const string TimePattern = @"^([01][0-9]|2[0-3]):[0-5][0-9]$";

    public UpsertStayRulesRequestValidator()
    {
        RuleFor(x => x.CheckInTime)
            .NotEmpty().WithMessage("Check-in time is required")
            .Matches(TimePattern).WithMessage("Check-in time must be HH:mm");

        RuleFor(x => x.CheckOutTime)
            .NotEmpty().WithMessage("Check-out time is required")
            .Matches(TimePattern).WithMessage("Check-out time must be HH:mm");

        RuleFor(x => x.CancellationPolicy)
            .Must(v => v != null && StayCancellation.All.Contains(v))
            .WithMessage("Cancellation must be Flexible, Moderate or Strict");
    }
}
