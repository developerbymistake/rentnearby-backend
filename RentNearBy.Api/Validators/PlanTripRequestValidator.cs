using FluentValidation;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class PlanTripRequestValidator : AbstractValidator<PlanTripRequest>
{
    public const string NoPlacesMessage = "Pick at least one place";
    public const string TooManyDaysMessage = "A trip can have up to 14 days";
    public const string InvalidCoordinatesMessage = "Invalid coordinates";
    public const string InvalidStartDateMessage = "Invalid start date";

    public static string TooManyPlacesMessage(int max) => $"A trip can have up to {max} places";

    public static bool IsValidStartDate(DateOnly date) => date.Year is >= 2000 and <= 2100;

    public PlanTripRequestValidator(IOptions<TripPlannerOptions>? options = null)
    {
        var maxSpots = Math.Clamp(options?.Value.MaxSpotsPerTrip ?? TripLimits.MaxSpots, 1, TripLimits.MaxSpots);

        RuleFor(x => x.CityId).NotEmpty().WithMessage("City is required");

        RuleFor(x => x.Mode)
            .Must(m => m != null && TripPlanModes.All.Contains(m)).WithMessage("Invalid plan mode");

        When(x => x.Mode == TripPlanModes.Auto, () =>
        {
            RuleFor(x => x.Spots)
                .Cascade(CascadeMode.Stop)
                .NotNull().WithMessage(NoPlacesMessage)
                .Must(s => s!.Count >= 1).WithMessage(NoPlacesMessage)
                .Must(s => s!.Count <= maxSpots).WithMessage(TooManyPlacesMessage(maxSpots))
                .WithErrorCode(TripErrorTypes.TooManySpots);

            RuleFor(x => x.Days).Null().WithMessage("Days are not used when planning automatically");

            RuleFor(x => x.Spots)
                .Must(s => s!.All(i => i != null)).WithMessage(NoPlacesMessage)
                .When(x => x.Spots != null);

            RuleForEach(x => x.Spots).SetValidator(new PlanSpotInputValidator()).When(x => x.Spots != null);
        });

        When(x => x.Mode == TripPlanModes.Recalculate, () =>
        {
            RuleFor(x => x.Days)
                .Cascade(CascadeMode.Stop)
                .NotNull().WithMessage(NoPlacesMessage)
                .Must(d => d!.Count >= 1).WithMessage(NoPlacesMessage)
                .Must(d => d!.Count <= TripLimits.MaxDays).WithMessage(TooManyDaysMessage)
                .Must(d => d!.All(i => i != null && i.Spots != null)).WithMessage(NoPlacesMessage);

            When(x => x.Days != null && x.Days.All(i => i != null && i.Spots != null), () =>
            {
                RuleFor(x => x.Days)
                    .Must(d => d!.All(i => i.Day >= 1 && i.Day <= TripLimits.MaxDays)).WithMessage(TooManyDaysMessage)
                    .Must(d => d!.Select(i => i.Day).Distinct().Count() == d!.Count).WithMessage("Each day can appear only once");

                RuleFor(x => x.Days)
                    .Cascade(CascadeMode.Stop)
                    .Must(d => d!.Sum(i => i.Spots.Count) >= 1).WithMessage(NoPlacesMessage)
                    .Must(d => d!.Sum(i => i.Spots.Count) <= maxSpots).WithMessage(TooManyPlacesMessage(maxSpots))
                    .WithErrorCode(TripErrorTypes.TooManySpots)
                    .Must(d => d!.All(i => i.Spots.All(s => s != null))).WithMessage(NoPlacesMessage);

                RuleForEach(x => x.Days).ChildRules(day =>
                {
                    day.RuleForEach(d => d.Spots).SetValidator(new PlanSpotInputValidator());
                });
            });

            RuleFor(x => x.Spots).Null().WithMessage("Places are sent per day when recalculating");
        });

        RuleFor(x => x.DayLimitMinutes)
            .InclusiveBetween(60, 900).WithMessage("Day length must be between 1 and 15 hours")
            .When(x => x.DayLimitMinutes != null);

        RuleFor(x => x.StartDate)
            .Must(d => IsValidStartDate(d!.Value)).WithMessage(InvalidStartDateMessage)
            .When(x => x.StartDate != null);
    }

    internal sealed class PlanSpotInputValidator : AbstractValidator<PlanSpotInput>
    {
        public PlanSpotInputValidator()
        {
            RuleFor(s => s.Id)
                .Must(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 64).WithMessage("Invalid place id");
            RuleFor(s => s.Name)
                .Must(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 120).WithMessage("Place name must be 1 to 120 characters");
            RuleFor(s => s)
                .Must(s => s.Latitude is >= -90 and <= 90 && s.Longitude is >= -180 and <= 180)
                .OverridePropertyName("Coordinates")
                .WithMessage(InvalidCoordinatesMessage);
            RuleFor(s => s.Category)
                .Must(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 32).WithMessage("Invalid place category");
            RuleFor(s => s.VisitMinutes)
                .InclusiveBetween(5, 720).WithMessage("Visit time must be between 5 minutes and 12 hours");
            RuleFor(s => s.ClosedDays)
                .Must(d => d!.Length <= 7 && d.All(v => v is >= 1 and <= 7)).WithMessage("Invalid closed days")
                .When(s => s.ClosedDays != null);
        }
    }
}
