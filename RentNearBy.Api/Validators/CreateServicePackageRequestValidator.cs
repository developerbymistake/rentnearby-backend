using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateServicePackageRequestValidator : AbstractValidator<CreateServicePackageRequest>
{
    public CreateServicePackageRequestValidator()
    {
        RuleFor(x => x.ServiceId).NotEmpty().WithMessage("ServiceId is required");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(150).WithMessage("Name must not exceed 150 characters");

        RuleFor(x => x.Price).GreaterThan(0).When(x => x.Price.HasValue);
        RuleFor(x => x.OriginalPrice).GreaterThan(0).When(x => x.OriginalPrice.HasValue);
        RuleFor(x => x.DiscountPercent).InclusiveBetween(1, 100).When(x => x.DiscountPercent.HasValue);
        RuleFor(x => x.DurationDays).GreaterThan(0).When(x => x.DurationDays.HasValue);
        RuleFor(x => x.DurationNights).GreaterThanOrEqualTo(0).When(x => x.DurationNights.HasValue);
        RuleFor(x => x.PriceUnit).MaximumLength(50).When(x => x.PriceUnit != null);

        // IsStartingAtPrice/DiscountPercent are only meaningful once a Price is actually set —
        // "Get Custom Quote" packages (Price == null) don't render either.
        RuleFor(x => x)
            .Must(x => x.Price.HasValue)
            .WithMessage("Price is required when IsStartingAtPrice is true")
            .When(x => x.IsStartingAtPrice);

        RuleFor(x => x)
            .Must(x => x.Price.HasValue)
            .WithMessage("Price is required when DiscountPercent is set")
            .When(x => x.DiscountPercent.HasValue);

        // Both the admin list's and the consumer app's discount-badge logic gate on
        // originalPrice > 0 (strikethrough reference price) — a DiscountPercent set without an
        // OriginalPrice silently produces a package with no visible discount badge.
        RuleFor(x => x)
            .Must(x => x.OriginalPrice.HasValue)
            .WithMessage("OriginalPrice is required when DiscountPercent is set")
            .When(x => x.DiscountPercent.HasValue);
    }
}
