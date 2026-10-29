using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateStayAcceptingRequestValidator : AbstractValidator<UpdateStayAcceptingRequest>
{
    public UpdateStayAcceptingRequestValidator()
    {
        RuleFor(x => x.AcceptingGuests)
            .NotNull().WithMessage("acceptingGuests is required.");
    }
}
