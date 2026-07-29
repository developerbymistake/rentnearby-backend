using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class WebEnquiryVerifyOtpRequestValidator : AbstractValidator<WebEnquiryVerifyOtpRequest>
{
    public WebEnquiryVerifyOtpRequestValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty().WithMessage("Token is required")
            .Must(t => Guid.TryParse(t, out _)).WithMessage("Invalid token");

        RuleFor(x => x.Otp)
            .NotEmpty().WithMessage("OTP is required")
            .Length(4).WithMessage("OTP must be 4 digits");
    }
}
