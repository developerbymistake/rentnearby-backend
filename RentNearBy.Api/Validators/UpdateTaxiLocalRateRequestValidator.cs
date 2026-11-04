using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpdateTaxiLocalRateRequestValidator : AbstractValidator<UpdateTaxiLocalRateRequest>
{
    public UpdateTaxiLocalRateRequestValidator()
    {
        RuleFor(x => x.DayRate)
            .InclusiveBetween(TaxiLimits.MinFare, TaxiLimits.MaxFare)
            .WithMessage(UpsertTaxiRoutesRequestValidator.FareRangeMessage);

        RuleFor(x => x.KmPerDay)
            .InclusiveBetween(0, TaxiLimits.LocalDayKmMax)
            .WithMessage("Km per day must be 0 to 1000");

        RuleFor(x => x.KmPerDay)
            .Null()
            .WithMessage("Add a day rate first")
            .When(x => !x.DayRate.HasValue);
    }
}
