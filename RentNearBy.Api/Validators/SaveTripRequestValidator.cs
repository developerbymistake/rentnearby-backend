using FluentValidation;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class SaveTripRequestValidator : AbstractValidator<SaveTripRequest>
{
    private const int MaxPrice = 9_900_000;

    public SaveTripRequestValidator(IOptions<TripPlannerOptions>? options = null)
    {
        var maxSpots = Math.Clamp(options?.Value.MaxSpotsPerTrip ?? TripLimits.MaxSpots, 1, TripLimits.MaxSpots);

        RuleFor(x => x.Title)
            .Must(t => t!.Trim().Length <= 80).WithMessage("Title can be up to 80 characters")
            .When(x => x.Title != null);

        RuleFor(x => x.Status)
            .Must(s => TripStatuses.All.Contains(s!)).WithMessage("Invalid trip status")
            .When(x => x.Status != null);

        RuleFor(x => x.Plan).NotNull().WithMessage("Plan is required");

        When(x => x.Plan != null, () =>
        {
            RuleFor(x => x.Plan.Version).Equal(TripPlan.CurrentVersion).WithMessage("Unsupported plan version");
            RuleFor(x => x.Plan.CityKey).GreaterThan(0).WithMessage("City is required");

            RuleFor(x => x.Plan.Settings).NotNull().WithMessage("Plan settings are required");
            RuleFor(x => x.Plan.Settings.DayLimitMinutes)
                .InclusiveBetween(60, 900).WithMessage("Day length must be between 1 and 15 hours")
                .When(x => x.Plan.Settings != null);
            RuleFor(x => x.Plan.Settings.StartDate)
                .Must(d => PlanTripRequestValidator.IsValidStartDate(d!.Value)).WithMessage(PlanTripRequestValidator.InvalidStartDateMessage)
                .When(x => x.Plan.Settings != null && x.Plan.Settings.StartDate != null);

            RuleFor(x => x.Plan.Days)
                .Cascade(CascadeMode.Stop)
                .NotNull().WithMessage("Add at least one day")
                .Must(d => d.Count >= 1).WithMessage("Add at least one day")
                .Must(d => d.Count <= TripLimits.MaxDays).WithMessage(PlanTripRequestValidator.TooManyDaysMessage)
                .Must(d => d.All(i => i != null && i.Spots != null)).WithMessage("Add at least one day")
                .Must(d => d.Select((i, n) => i.Day == n + 1).All(ok => ok)).WithMessage("Days must be numbered from 1 in order");

            When(x => x.Plan.Days != null && x.Plan.Days.All(i => i != null && i.Spots != null), () =>
            {
                RuleFor(x => x.Plan.Days)
                    .Cascade(CascadeMode.Stop)
                    .Must(d => d.Sum(i => i.Spots.Count) <= maxSpots)
                    .WithMessage(PlanTripRequestValidator.TooManyPlacesMessage(maxSpots))
                    .WithErrorCode(TripErrorTypes.TooManySpots)
                    .Must(d => d.All(i => i.Spots.All(s => s != null))).WithMessage(PlanTripRequestValidator.NoPlacesMessage);

                // Zero places is allowed only while the trip is a draft.
                RuleFor(x => x.Plan.Days)
                    .Must(d => d.Sum(i => i.Spots.Count) >= 1).WithMessage(PlanTripRequestValidator.NoPlacesMessage)
                    .When(x => x.Status == TripStatuses.Planned);

                RuleForEach(x => x.Plan.Days).ChildRules(day =>
                {
                    day.RuleForEach(d => d.Spots).SetValidator(new TripSpotValidator());

                    day.RuleFor(d => d.Taxi!.Name)
                        .Must(n => n == null || n.Length <= 120).WithMessage("Taxi name can be up to 120 characters")
                        .When(d => d.Taxi != null);
                    day.RuleFor(d => d.Taxi!.VehicleTypeName)
                        .Must(n => n == null || n.Length <= 120).WithMessage("Vehicle type can be up to 120 characters")
                        .When(d => d.Taxi != null);
                    day.RuleFor(d => d.Taxi!.Fare)
                        .InclusiveBetween(0, MaxPrice).WithMessage("Invalid taxi fare")
                        .When(d => d.Taxi != null && d.Taxi.Fare != null);

                    day.RuleFor(d => d.Stay!.Name)
                        .Must(n => n == null || n.Length <= 120).WithMessage("Stay name can be up to 120 characters")
                        .When(d => d.Stay != null);
                    day.RuleFor(d => d.Stay!.PricePerNight)
                        .InclusiveBetween(0, MaxPrice).WithMessage("Invalid stay price")
                        .When(d => d.Stay != null && d.Stay.PricePerNight != null);
                    day.RuleFor(d => d.Stay!.SameAsDay)
                        .Must((d, v) => v >= 1 && v <= d.Day - 1).WithMessage("Same stay must point to an earlier day")
                        .When(d => d.Stay != null && d.Stay.SameAsDay != null);
                });
            });
        });
    }

    internal sealed class TripSpotValidator : AbstractValidator<TripSpot>
    {
        public TripSpotValidator()
        {
            RuleFor(s => s.Id)
                .Must(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 64).WithMessage("Invalid place id");
            RuleFor(s => s.Name)
                .Must(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 120).WithMessage("Place name must be 1 to 120 characters");
            RuleFor(s => s)
                .Must(s => s.Latitude is >= -90 and <= 90 && s.Longitude is >= -180 and <= 180)
                .OverridePropertyName("Coordinates")
                .WithMessage(PlanTripRequestValidator.InvalidCoordinatesMessage);
            RuleFor(s => s.Category)
                .Must(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 32).WithMessage("Invalid place category");
            RuleFor(s => s.VisitMinutes)
                .InclusiveBetween(5, 720).WithMessage("Visit time must be between 5 minutes and 12 hours");
            RuleFor(s => s.ClosedDays)
                .Must(d => d!.Length <= 7 && d.All(v => v is >= 1 and <= 7)).WithMessage("Invalid closed days")
                .When(s => s.ClosedDays != null);
            RuleFor(s => s.ImageUrl)
                .Must(v => v!.Length <= 1000).WithMessage("Image link is too long")
                .When(s => s.ImageUrl != null);
        }
    }
}
