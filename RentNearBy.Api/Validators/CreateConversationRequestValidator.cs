using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateConversationRequestValidator : AbstractValidator<CreateConversationRequest>
{
    public CreateConversationRequestValidator()
    {
        RuleFor(x => x.ListingType)
            .Must(t => t is "Room" or "Plot").WithMessage("ListingType must be 'Room' or 'Plot'");

        RuleFor(x => x.ListingId).NotEmpty();
    }
}
