using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class SaveTripRequestValidator : AbstractValidator<SaveTripRequest>
{
    private const int MaxNameLength = 120;

    public const int MaxDismissed = 100;
    public const string DismissedMessage = "Invalid dismissed list";
    public const string LeaveOnMessage = "Invalid leave-on";
    public const string OwnCarsMessage = "Invalid own cars";

    internal static bool IsValidDismissed(List<Guid>? ids)
        => ids == null || (ids.Count <= MaxDismissed && ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count);

    internal static bool IsValidOwnCars(List<int>? cars)
        => cars == null || (cars.All(c => c is >= 1 and <= JourneyRules.MaxLegs) && cars.Distinct().Count() == cars.Count);

    internal static bool IsValidCoordinates(double? lat, double? lon)
        => (lat == null && lon == null)
           || (lat is >= -90 and <= 90 && lon is >= -180 and <= 180);

    public const string MaxLegsMessage = "Up to 4 cities in one trip";
    public const string LegOrderMessage = "Invalid city order";
    public const string DuplicateCityMessage = "A city can be in a trip once";
    public const string FirstArrivalMessage = "The first city has no travel to it";
    public const string DaysFollowLegsMessage = "Days must follow the cities in order";
    public const string MarkMessage = "Invalid mark";
    public const string CityRequiredMessage = "City is required";
    public const string VersionMessage = "Unsupported plan version";
    public const string TravelMessage = "Invalid travel";
    public const string RoadTimeMessage = "Invalid road time";
    public const string TimeMessage = "Invalid time";

    internal static bool DaysFollowLegs(IReadOnlyList<TripDay> days, int legCount)
    {
        if (days.Count == 0) return true;
        if (days[0].Leg != 1 || days[^1].Leg != legCount) return false;
        for (var i = 1; i < days.Count; i++)
            if (days[i].Leg - days[i - 1].Leg is not (0 or 1)) return false;
        return true;
    }

    public SaveTripRequestValidator(IOptions<TripPlannerOptions>? options = null)
    {
        var maxSpots = Math.Clamp(options?.Value.MaxSpotsPerTrip ?? TripLimits.MaxSpots, 1, TripLimits.MaxSpots);

        RuleFor(x => x.Title)
            .Must(t => t!.Trim().Length <= 80).WithMessage("Title can be up to 80 characters")
            .When(x => x.Title != null);

        RuleFor(x => x.Status)
            .Must(s => TripStatuses.Savable.Contains(s!)).WithMessage("Invalid trip status")
            .When(x => x.Status != null);

        RuleFor(x => x.ClientTripId)
            .Must(id => id != Guid.Empty).WithMessage("Invalid client trip id")
            .When(x => x.ClientTripId != null);

        RuleFor(x => x.Plan).NotNull().WithMessage("Plan is required");

        When(x => x.Plan != null, () =>
        {
            RuleFor(x => x.Plan.Version)
                .Must(v => TripPlan.SupportedVersions.Contains(v)).WithMessage(VersionMessage);
            RuleFor(x => x.Plan.CityKey).GreaterThan(0).WithMessage(CityRequiredMessage);
            RuleFor(x => x.Plan.TaxiMark!).SetValidator(new TripRowMarkValidator()).When(x => x.Plan.TaxiMark != null);
            RuleFor(x => x.Plan.DismissedEnquiryIds).Must(IsValidDismissed).WithMessage(DismissedMessage);

            RuleFor(x => x.Plan.Settings).NotNull().WithMessage("Plan settings are required");
            RuleFor(x => x.Plan.Settings.DayLimitMinutes)
                .InclusiveBetween(60, 900).WithMessage("Day length must be between 1 and 15 hours")
                .When(x => x.Plan.Settings != null);
            RuleFor(x => x.Plan.Settings.StartDate)
                .Must(d => PlanTripRequestValidator.IsValidStartDate(d!.Value)).WithMessage(PlanTripRequestValidator.InvalidStartDateMessage)
                .When(x => x.Plan.Settings != null && x.Plan.Settings.StartDate != null);
            RuleFor(x => x.Plan.Settings).SetValidator(new TripSettingsValidator()).When(x => x.Plan.Settings != null);
            RuleFor(x => x.Plan.Steps!).SetValidator(new TripStepsValidator()).When(x => x.Plan.Steps != null);

            RuleFor(x => x.Plan.Legs)
                .Must(l => l is { Count: > 0 }).WithMessage(CityRequiredMessage);

            When(x => x.Plan.Legs is { Count: > 0 }, () =>
            {
                RuleFor(x => x.Plan.Legs!)
                    .Cascade(CascadeMode.Stop)
                    .Must(l => l.Count <= TripLimits.MaxLegs).WithMessage(MaxLegsMessage)
                    .Must(l => l.Select((g, i) => g.Index == i + 1).All(ok => ok)).WithMessage(LegOrderMessage)
                    .Must(l => l.Skip(1).All(g => g.CityKey > 0)).WithMessage(CityRequiredMessage)
                    .Must(l => l.Where(g => g.CityKey > 0).Select(g => g.CityKey).Distinct().Count() == l.Count(g => g.CityKey > 0))
                    .WithMessage(DuplicateCityMessage)
                    .Must(l => l[0].Arrival == null).WithMessage(FirstArrivalMessage);
                RuleForEach(x => x.Plan.Legs!).SetValidator(new TripCityLegValidator());
            });

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
                    .Must(d => d.All(i => i.Spots.Count <= TripLimits.MaxSpotsPerDay))
                    .WithMessage(PlanTripRequestValidator.TooManyPerDayMessage)
                    .WithErrorCode(TripErrorTypes.TooManySpotsPerDay)
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
                });

                RuleFor(x => x.Plan.Days)
                    .Must((x, d) => DaysFollowLegs(d, x.Plan.Legs!.Count)).WithMessage(DaysFollowLegsMessage)
                    .When(x => x.Plan.Legs is { Count: > 0 });
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

    internal sealed class TripSettingsValidator : AbstractValidator<TripSettings>
    {
        public TripSettingsValidator()
        {
            RuleFor(s => s.Adults)
                .InclusiveBetween(1, 30).WithMessage("Adults must be 1 to 30")
                .When(s => s.Adults != null);
            RuleFor(s => s.Kids)
                .InclusiveBetween(0, 20).WithMessage("Kids must be 0 to 20")
                .When(s => s.Kids != null);
            RuleFor(s => s.MaxDays)
                .InclusiveBetween(1, TripLimits.MaxDays).WithMessage(PlanTripRequestValidator.MaxDaysMessage)
                .When(s => s.MaxDays != null);
        }
    }

    protected override bool PreValidate(ValidationContext<SaveTripRequest> context, ValidationResult result)
    {
        var plan = context.InstanceToValidate?.Plan;
        plan?.Legs?.RemoveAll(l => l == null);
        if (plan?.Legs is { Count: > 0 }) plan.Normalize();
        return true;
    }

    internal sealed class TripRowMarkValidator : AbstractValidator<TripRowMark>
    {
        public TripRowMarkValidator()
        {
            RuleFor(m => m.Status).Must(v => v != null && TripRowMarkStatuses.All.Contains(v)).WithMessage(MarkMessage);
        }
    }

    internal sealed class TripCityLegValidator : AbstractValidator<TripCityLeg>
    {
        public TripCityLegValidator()
        {
            RuleFor(l => l.CityName)
                .Must(n => n != null && n.Trim().Length is >= 1 and <= MaxNameLength)
                .WithMessage("City name must be 1 to 120 characters");
            RuleFor(l => l)
                .Must(l => l.Latitude is >= -90 and <= 90 && l.Longitude is >= -180 and <= 180)
                .OverridePropertyName("Coordinates")
                .WithMessage(PlanTripRequestValidator.InvalidCoordinatesMessage);
            RuleFor(l => l.Arrival!).SetValidator(new TripTravelValidator()).When(l => l.Arrival != null);
            RuleFor(l => l.StayMark!).SetValidator(new TripRowMarkValidator()).When(l => l.StayMark != null);
            RuleFor(l => l.TravelMark!).SetValidator(new TripRowMarkValidator()).When(l => l.TravelMark != null);
            RuleFor(l => l.TravelMark)
                .Null().WithMessage(TravelMessage)
                .When(l => l.Index == 1);
            RuleFor(l => l.Arrival)
                .Null().WithMessage(TravelMessage)
                .When(l => l.TravelMark != null);
        }
    }

    internal sealed class TripTravelValidator : AbstractValidator<TripTravel>
    {
        public TripTravelValidator()
        {
            RuleFor(t => t.FromName).Must(n => n == null || n.Length <= MaxNameLength).WithMessage(TravelMessage);
            RuleFor(t => t.RoadMinutes)
                .Must(m => m == null || m.Value is >= 0 and <= MaxTravelRoadMinutes).WithMessage(RoadTimeMessage);
            RuleFor(t => t.JourneyId)
                .Must(id => id is { } v && v != Guid.Empty)
                .WithMessage(TravelMessage);
            RuleFor(t => t.PlanKey).Must(k => k == null || TaxiWaysRules.IsKey(k)).WithMessage(TravelMessage);
            RuleFor(t => t.Cars).Must(c => c == null || c.Value is >= 1 and <= JourneyRules.MaxLegs).WithMessage(TravelMessage);
            RuleFor(t => t.ListedTotal).Must(v => v == null || v.Value is >= 0 and <= MaxTravelListedTotal).WithMessage(TravelMessage);
            RuleFor(t => t.OwnCars).Must(IsValidOwnCars).WithMessage(OwnCarsMessage);
        }

        public const int MaxTravelRoadMinutes = 900;
        public const int MaxTravelListedTotal = 10_000_000;
    }

    internal sealed class TripStepsValidator : AbstractValidator<TripSteps>
    {
        public const string StartFromMessage = "Starting from can be up to 80 characters";
        public const string GoingHomeMessage = "Going home to can be up to 80 characters";
        public const string StatusMessage = "Invalid step status";
        public const string DateMessage = "Invalid travel date";

        public TripStepsValidator()
        {
            RuleFor(s => s.StartFrom!.Name)
                .Must(n => n == null || n.Trim().Length <= TripStepRules.NameMax).WithMessage(StartFromMessage)
                .When(s => s.StartFrom != null);
            RuleFor(s => s.StartFrom!)
                .Must(p => p.Lat is >= -90 and <= 90 && p.Lon is >= -180 and <= 180)
                .OverridePropertyName("Coordinates")
                .WithMessage(PlanTripRequestValidator.InvalidCoordinatesMessage)
                .When(s => s.StartFrom != null);
            RuleFor(s => s.GoingHomeTo!.Name)
                .Must(n => n == null || n.Trim().Length <= TripStepRules.NameMax).WithMessage(GoingHomeMessage)
                .When(s => s.GoingHomeTo != null);
            RuleFor(s => s.GoingHomeTo!)
                .Must(p => p.Lat is >= -90 and <= 90 && p.Lon is >= -180 and <= 180)
                .OverridePropertyName("GoingHomeCoordinates")
                .WithMessage(PlanTripRequestValidator.InvalidCoordinatesMessage)
                .When(s => s.GoingHomeTo != null);
            RuleFor(s => s.GettingThere!).SetValidator(new TripStepValidator()).When(s => s.GettingThere != null);
            RuleFor(s => s.ComingBack!).SetValidator(new TripStepValidator()).When(s => s.ComingBack != null);
            RuleFor(s => s.GettingThere!.LeaveOn).Null().WithMessage(LeaveOnMessage).When(s => s.GettingThere != null);
            RuleFor(s => s.ComingBack!.LeaveOn)
                .Must(v => v == null || TripLeaveOn.All.Contains(v)).WithMessage(LeaveOnMessage)
                .When(s => s.ComingBack != null);
        }
    }

    internal sealed class TripStepValidator : AbstractValidator<TripStep>
    {
        public TripStepValidator()
        {
            RuleFor(s => s.Status)
                .Must(v => TripStepStatuses.All.Contains(v!)).WithMessage(TripStepsValidator.StatusMessage)
                .When(s => s.Status != null);
            RuleFor(s => s.OwnCars).Must(IsValidOwnCars).WithMessage(OwnCarsMessage);
            RuleFor(s => s.Date)
                .Must(d => PlanTripRequestValidator.IsValidStartDate(d!.Value)).WithMessage(TripStepsValidator.DateMessage)
                .When(s => s.Date != null);
        }
    }
}
