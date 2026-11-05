using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateConversationRequestValidator : AbstractValidator<CreateConversationRequest>
{
    public CreateConversationRequestValidator()
    {
        RuleFor(x => x.ListingType)
            .Must(t => t is "Stay").WithMessage("ListingType must be 'Stay'");

        RuleFor(x => x.ListingId).NotEmpty();
    }
}
