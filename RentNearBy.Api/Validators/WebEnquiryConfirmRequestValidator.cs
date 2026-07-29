using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class WebEnquiryConfirmRequestValidator : AbstractValidator<WebEnquiryConfirmRequest>
{
    public WebEnquiryConfirmRequestValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty().WithMessage("Token is required")
            .Must(t => Guid.TryParse(t, out _)).WithMessage("Invalid token");
    }
}
