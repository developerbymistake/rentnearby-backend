using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpdateStayRoomPriceRequestValidator : AbstractValidator<UpdateStayRoomPriceRequest>
{
    public UpdateStayRoomPriceRequestValidator()
    {
        RuleFor(x => x.PricePerNight)
            .InclusiveBetween(StayLimits.MinPrice, StayLimits.MaxPrice)
            .WithMessage("Price per night must be between ₹100 and ₹99,00,000");
    }
}
