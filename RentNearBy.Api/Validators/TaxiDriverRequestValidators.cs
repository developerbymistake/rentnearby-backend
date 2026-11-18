using FluentValidation;
using RentNearBy.Core.DTOs.TaxiRequests;

namespace RentNearBy.Api.Validators;

// D8 query window (from, to).
public sealed record DriverBookingsWindow(DateOnly? From, DateOnly? To);

public class DriverBookingsWindowValidator : AbstractValidator<DriverBookingsWindow>
{
    public const int MaxDays = 92;

    public DriverBookingsWindowValidator()
    {
        RuleFor(x => x.From).NotNull().WithMessage("From date is required");
        RuleFor(x => x.To).NotNull().WithMessage("To date is required");
        RuleFor(x => x)
            .Must(w => w.To!.Value >= w.From!.Value)
            .When(w => w.From is not null && w.To is not null)
            .WithMessage("To date must not be before from date");
        RuleFor(x => x)
            .Must(w => w.To!.Value.DayNumber - w.From!.Value.DayNumber < MaxDays)
            .When(w => w.From is not null && w.To is not null)
            .WithMessage($"Choose at most {MaxDays} days");
    }
}

// D11 device report.
public class DriverDeviceStateValidator : AbstractValidator<DriverDeviceStateDto>
{
    public const int ManufacturerMax = 64;

    public DriverDeviceStateValidator()
    {
        RuleFor(x => x.AppBuild).InclusiveBetween(1, 1_000_000).WithMessage("App build is invalid");
        RuleFor(x => x.AndroidSdk).InclusiveBetween(1, 1000).WithMessage("Android version is invalid");
        RuleFor(x => x.Manufacturer).MaximumLength(ManufacturerMax).WithMessage($"Manufacturer must be at most {ManufacturerMax} characters");
    }
}
