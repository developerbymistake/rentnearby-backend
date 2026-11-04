using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpdateStayAdvanceDetailsRequestValidator : AbstractValidator<UpdateStayAdvanceDetailsRequest>
{
    public UpdateStayAdvanceDetailsRequestValidator()
    {
        RuleFor(x => x.UpiId)
            .Must(v => v == null || v.Trim().Length == 0 || TripAdvanceRules.IsValidUpi(v))
            .WithMessage("Enter a UPI ID like name@bank");

        RuleFor(x => x.WhatsAppNumber)
            .Must(v => v == null || v.Trim().Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(v.Trim(), TripExecutionValidation.PhonePattern))
            .WithMessage(TripExecutionValidation.PhoneMessage);
    }
}
