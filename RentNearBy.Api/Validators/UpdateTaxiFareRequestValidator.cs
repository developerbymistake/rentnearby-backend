using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpdateTaxiFareRequestValidator : AbstractValidator<UpdateTaxiFareRequest>
{
    public UpdateTaxiFareRequestValidator()
    {
        RuleFor(x => x.Fare)
            .InclusiveBetween(TaxiLimits.MinFare, TaxiLimits.MaxFare)
            .WithMessage(UpsertTaxiRoutesRequestValidator.FareRangeMessage);
    }
}
