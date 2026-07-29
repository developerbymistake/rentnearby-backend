using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class PhoneLoginSendOtpRequestValidator : AbstractValidator<PhoneLoginSendOtpRequest>
{
    public PhoneLoginSendOtpRequestValidator()
    {
        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage("Phone number is required")
            .Matches(@"^[6-9]\d{9}$").WithMessage("Phone number must be a valid 10-digit Indian mobile number");
    }
}
