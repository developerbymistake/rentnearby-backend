using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class AdminUpdateInquiryStatusRequestValidator : AbstractValidator<AdminUpdateInquiryStatusRequest>
{
    private static readonly string[] AllowedStatuses =
    [
        InquiryStatuses.Submitted, InquiryStatuses.Contacted, InquiryStatuses.Confirmed,
        InquiryStatuses.Cancelled, InquiryStatuses.Rejected
    ];

    public AdminUpdateInquiryStatusRequestValidator()
    {
        RuleFor(x => x.Status)
            .NotEmpty().WithMessage("Status is required")
            .Must(s => AllowedStatuses.Contains(s)).WithMessage("Invalid status");

        RuleFor(x => x.Note).MaximumLength(500).When(x => x.Note != null);
    }
}
