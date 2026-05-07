using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class SendOtpRequestValidator : AbstractValidator<SendOtpRequest>
{
    public SendOtpRequestValidator()
    {
        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage("Phone number is required")
            .Matches(@"^\d{10}$").WithMessage("Phone number must be 10 digits");
    }
}
