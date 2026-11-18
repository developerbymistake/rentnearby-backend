using FluentValidation;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Api.Validators;

// 400 rules of the traveller bodies (contract section 5). Error code = error.type the handler answers with.
// Rules that need the stored trip or request (days in the past, chip needed for a booked request) stay in TaxiRequestCreateRules.
public class CreateTaxiRequestValidator : AbstractValidator<CreateTaxiRequestDto>
{
    public const string DayStatMessage = "Day figures are out of range";

    public CreateTaxiRequestValidator()
    {
        RuleFor(x => x.ClientRequestId)
            .Must(id => id != Guid.Empty)
            .When(x => x.ClientRequestId != null)
            .WithMessage("Invalid request id");

        RuleFor(x => x)
            .Must(x => x.Pickup?.On == true || x.Sightseeing?.On == true || x.Drop?.On == true)
            .WithErrorCode(TaxiRequestErrorTypes.TaxiSectionsRequired)
            .WithMessage(TaxiRequestErrorTypes.Messages[TaxiRequestErrorTypes.TaxiSectionsRequired]);

        RuleFor(x => x.Seats)
            .Must(TaxiRequestSeats.IsValid)
            .WithMessage(TaxiRequestCreateRules.SeatsMessage);

        RuleFor(x => x.Sightseeing.Days)
            .Must(d => d != null && d.Count >= 1 && d.Distinct().Count() == d.Count && d.All(v => v is >= 1 and <= TripLimits.MaxDays))
            .When(x => x.Sightseeing?.On == true)
            .WithErrorCode(TaxiRequestErrorTypes.SightseeingDaysInvalid)
            .WithMessage(TaxiRequestErrorTypes.Messages[TaxiRequestErrorTypes.SightseeingDaysInvalid]);

        RuleFor(x => x.DayStats)
            .Must(s => s == null || (s.Count <= TripLimits.MaxDays && s.All(IsDayStat) && s.Select(d => d.Day).Distinct().Count() == s.Count))
            .WithMessage(DayStatMessage);
    }

    private static bool IsDayStat(TaxiDayStatDto s)
        => s.Day is >= 1 and <= TripLimits.MaxDays
           && s.Km is >= 0 and <= TaxiDispatchRules.MaxDayKm
           && s.Minutes is >= 0 and <= TaxiDispatchRules.MaxDayMinutes;
}

public class CancelTaxiRequestValidator : AbstractValidator<CancelTaxiRequestDto>
{
    public CancelTaxiRequestValidator()
    {
        RuleFor(x => x.Reason)
            .Must(r => r != null && RequestEndReasons.TravellerChoices.Contains(r))
            .WithMessage(TaxiRequestCreateRules.ReasonMessage);

        RuleFor(x => x.Chip)
            .Must(ReasonChips.IsValid)
            .When(x => x.Chip != null)
            .WithMessage(TaxiRequestCreateRules.ChipMessage);
    }
}

public class RateTaxiRequestValidator : AbstractValidator<RateTaxiRequestDto>
{
    public const string StarsMessage = "Rating must be 1 to 5 stars";

    public RateTaxiRequestValidator()
    {
        RuleFor(x => x.Stars).InclusiveBetween(1, 5).WithMessage(StarsMessage);
    }
}
