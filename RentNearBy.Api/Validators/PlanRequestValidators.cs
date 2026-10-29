using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class QuoteRequestValidator : AbstractValidator<QuoteRequest>
{
    public QuoteRequestValidator()
    {
        RuleFor(x => x.PlanId).NotEmpty();
        RuleFor(x => x.CycleMonths).Must(PlanCycles.IsValid).WithMessage("CycleMonths must be 1, 3 or 12");
        RuleFor(x => x.CouponCode).MaximumLength(PlanRules.MaxCouponCodeLength);
    }
}

public class CreatePlanOrderRequestValidator : AbstractValidator<CreatePlanOrderRequest>
{
    public CreatePlanOrderRequestValidator()
    {
        RuleFor(x => x.PlanId).NotEmpty();
        RuleFor(x => x.CycleMonths).Must(PlanCycles.IsValid).WithMessage("CycleMonths must be 1, 3 or 12");
        RuleFor(x => x.CouponCode).MaximumLength(PlanRules.MaxCouponCodeLength);
    }
}

public class VerifyPlanPurchaseRequestValidator : AbstractValidator<VerifyPlanPurchaseRequest>
{
    public VerifyPlanPurchaseRequestValidator()
    {
        RuleFor(x => x.RazorpayOrderId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.RazorpayPaymentId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.RazorpaySignature).NotEmpty().MaximumLength(256);
    }
}

public class StartTrialRequestValidator : AbstractValidator<StartTrialRequest>
{
    public StartTrialRequestValidator()
    {
        RuleFor(x => x.PlanId).NotEmpty();
    }
}

public class AdminGrantPlanRequestValidator : AbstractValidator<AdminGrantPlanRequest>
{
    public AdminGrantPlanRequestValidator()
    {
        RuleFor(x => x.PlanId).NotEmpty();
        RuleFor(x => x.Days).InclusiveBetween(1, PlanRules.MaxGrantDays);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(64);
    }
}

public class AdminRevokePlanRequestValidator : AbstractValidator<AdminRevokePlanRequest>
{
    public AdminRevokePlanRequestValidator()
    {
        RuleFor(x => x.UserPlanId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
    }
}

public class AdminRefundPurchaseRequestValidator : AbstractValidator<AdminRefundPurchaseRequest>
{
    public AdminRefundPurchaseRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public class SwapLiveRequestValidator : AbstractValidator<SwapLiveRequest>
{
    public SwapLiveRequestValidator()
    {
        RuleFor(x => x.PauseListingId).NotEmpty();
    }
}
