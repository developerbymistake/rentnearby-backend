using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class VerifyPlayPurchaseRequestValidator : AbstractValidator<VerifyPlayPurchaseRequest>
{
    public VerifyPlayPurchaseRequestValidator()
    {
        RuleFor(x => x.PurchaseToken).NotEmpty().Length(20, 1024);
        RuleFor(x => x.ProductId).NotEmpty().Matches(UpsertPlanRequestValidator.PlayProductIdPattern)
            .WithMessage("ProductId is not a valid Play product id");
    }
}

public class RestorePlayPurchasesRequestValidator : AbstractValidator<RestorePlayPurchasesRequest>
{
    public RestorePlayPurchasesRequestValidator()
    {
        RuleFor(x => x.Purchases).NotNull()
            .Must(p => p is { Count: >= 1 and <= PlayRules.RestoreMaxTokens })
            .WithMessage($"Purchases must contain 1 to {PlayRules.RestoreMaxTokens} items");
        RuleForEach(x => x.Purchases).ChildRules(i =>
        {
            i.RuleFor(v => v.PurchaseToken).NotEmpty().Length(20, 1024);
            i.RuleFor(v => v.ProductId).NotEmpty().Matches(UpsertPlanRequestValidator.PlayProductIdPattern)
                .WithMessage("ProductId is not a valid Play product id");
        });
    }
}
