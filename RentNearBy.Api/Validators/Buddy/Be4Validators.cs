using FluentValidation;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Api.Validators.Buddy;

public class SaidPaidRequestValidator : AbstractValidator<SaidPaidRequestDto>
{
    public SaidPaidRequestValidator()
    {
        RuleFor(x => x.OpId).NotEqual(Guid.Empty).WithMessage("opId is required");
        RuleFor(x => x.ToUserId).NotEqual(Guid.Empty).WithMessage("toUserId is required");
        RuleFor(x => x.Amount).Must(TripPaymentRules.IsValidAmount).WithMessage("Amount must be 1 to 5000000");
    }
}

public class RemindRequestValidator : AbstractValidator<RemindRequestDto>
{
    public RemindRequestValidator()
    {
        RuleFor(x => x.FromUserId).NotEqual(Guid.Empty).WithMessage("fromUserId is required");
    }
}
