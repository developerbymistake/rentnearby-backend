using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class AdminCloseTripEnquiryRequestValidator : AbstractValidator<AdminCloseTripEnquiryRequest>
{
    public const string NoteMessage = "Note must be 10 to 300 characters";

    public AdminCloseTripEnquiryRequestValidator()
    {
        RuleFor(x => x.Note)
            .Must(v => v != null && v.Trim().Length is >= 10 and <= 300)
            .WithMessage(NoteMessage);
    }
}
