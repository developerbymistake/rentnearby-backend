using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class SaveTripRequestValidator : AbstractValidator<SaveTripRequest>
{
    private const int MaxPrice = 9_900_000;
    private const int MaxShortlist = 3;
    private const int MaxNameLength = 120;
    private const int MaxNoteLength = 300;
    private const int MaxPlaceLength = 80;
    private const int MaxOwnNameLength = 80;
    private const string OwnNameMessage = "Name must be 1 to 80 characters";
    private const string ShortlistMessage = "Up to 3 shortlisted per day";
    private const string NoteMessage = "Note can be up to 300 characters";
    private const string PhoneMessage = "Enter a 10 digit phone";
    private const string PickupTimeMessage = "Invalid pickup time";
    private const string PickupPlaceMessage = "Pickup place can be up to 80 characters";
    private static readonly Regex PickupTimePattern = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.CultureInvariant);
    private static readonly Regex PhonePattern = new(@"^[6-9]\d{9}$", RegexOptions.CultureInvariant);

    internal static bool IsValidPickupTime(string? value) => value == null || PickupTimePattern.IsMatch(value);

    internal static bool IsValidPickupPlace(string? value)
        => value == null || value.Trim().Length is >= 1 and <= MaxPlaceLength;

    internal static bool IsValidPhone(string? value) => value == null || PhonePattern.IsMatch(value);

    internal static bool IsValidNote(string? value) => value == null || value.Length <= MaxNoteLength;

    internal static bool IsValidOwnName(string? value) => value != null && value.Trim().Length is >= 1 and <= MaxOwnNameLength;

    internal static bool IsValidCoordinates(double? lat, double? lon)
        => (lat == null && lon == null)
           || (lat is >= -90 and <= 90 && lon is >= -180 and <= 180);

    public const string MaxLegsMessage = "Up to 4 cities in one trip";
    public const string LegOrderMessage = "Invalid city order";
    public const string DuplicateCityMessage = "A city can be in a trip once";
    public const string FirstArrivalMessage = "The first city has no travel to it";
    public const string DaysFollowLegsMessage = "Days must follow the cities in order";
    public const string SameCityStayMessage = "Same stay must be an earlier night in the same city";
    public const string TravelMessage = "Invalid travel";
    public const string RoadTimeMessage = "Invalid road time";
    public const string TimeMessage = "Invalid time";

    // A booking copy must list distinct days of this plan in one city, including the day it sits on.
    internal static bool BookingDaysValid(TripDay day, TripPlan plan)
    {
        var booking = day.Taxi?.Booking;
        if (booking == null) return true;
        var days = booking.Days;
        if (days == null || days.Length == 0) return false;
        var dayCount = plan.Days.Count;
        var leg = plan.LegIndexOf(day.Day);
        return days.Distinct().Count() == days.Length
               && days.All(n => n >= 1 && n <= dayCount && plan.LegIndexOf(n) == leg)
               && days.Contains(day.Day);
    }

    // Out-of-range targets are refused by the earlier-day rule.
    internal static bool SameAsDayInLeg(TripPlan plan, TripDay day, int? target)
        => target is not { } t || t < 1 || t >= day.Day || plan.LegIndexOf(t) == plan.LegIndexOf(day.Day);

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
                .Must(v => TripPlan.SupportedVersions.Contains(v)).WithMessage("Unsupported plan version");
            RuleFor(x => x.Plan.CityKey).GreaterThan(0).WithMessage("City is required");

            RuleFor(x => x.Plan.Settings).NotNull().WithMessage("Plan settings are required");
            RuleFor(x => x.Plan.Settings.DayLimitMinutes)
                .InclusiveBetween(60, 900).WithMessage("Day length must be between 1 and 15 hours")
                .When(x => x.Plan.Settings != null);
            RuleFor(x => x.Plan.Settings.StartDate)
                .Must(d => PlanTripRequestValidator.IsValidStartDate(d!.Value)).WithMessage(PlanTripRequestValidator.InvalidStartDateMessage)
                .When(x => x.Plan.Settings != null && x.Plan.Settings.StartDate != null);
            RuleFor(x => x.Plan.Settings).SetValidator(new TripSettingsValidator()).When(x => x.Plan.Settings != null);
            RuleFor(x => x.Plan.Steps!).SetValidator(new TripStepsValidator()).When(x => x.Plan.Steps != null);

            // Legs exist here: PreValidate normalised the plan (a legless body is one leg).
            When(x => x.Plan.Legs != null, () =>
            {
                RuleFor(x => x.Plan.Legs!)
                    .Cascade(CascadeMode.Stop)
                    .Must(l => l.Count is >= 1 and <= TripLimits.MaxLegs).WithMessage(MaxLegsMessage)
                    .Must(l => l.Select((g, i) => g.Index == i + 1).All(ok => ok)).WithMessage(LegOrderMessage)
                    .Must(l => l.Skip(1).All(g => g.CityKey > 0)).WithMessage("City is required")
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

                    day.RuleFor(d => d.Taxi!).SetValidator(new TripTaxiChoiceValidator()).When(d => d.Taxi != null);
                    day.RuleFor(d => d.Stay!).SetValidator(new TripStayChoiceValidator()).When(d => d.Stay != null);

                    day.RuleFor(d => d.StayShortlist)
                        .Must(l => l!.Count <= MaxShortlist).WithMessage(ShortlistMessage)
                        .Must(l => l!.All(i => i != null)).WithMessage(ShortlistMessage)
                        .When(d => d.StayShortlist != null);
                    day.RuleForEach(d => d.StayShortlist)
                        .SetValidator(new TripStayShortlistItemValidator())
                        .When(d => d.StayShortlist != null && d.StayShortlist.All(i => i != null));
                    day.RuleForEach(d => d.StayShortlist)
                        .Must((d, i) => i.SameAsDay == null || (i.SameAsDay >= 1 && i.SameAsDay <= d.Day - 1))
                        .WithMessage("Same stay must point to an earlier day")
                        .When(d => d.StayShortlist != null && d.StayShortlist.All(i => i != null));

                    day.RuleFor(d => d.TaxiShortlist)
                        .Must(l => l!.Count <= MaxShortlist).WithMessage(ShortlistMessage)
                        .Must(l => l!.All(i => i != null)).WithMessage(ShortlistMessage)
                        .When(d => d.TaxiShortlist != null);
                    day.RuleForEach(d => d.TaxiShortlist)
                        .SetValidator(new TripTaxiShortlistItemValidator())
                        .When(d => d.TaxiShortlist != null && d.TaxiShortlist.All(i => i != null));
                });

                RuleForEach(x => x.Plan.Days)
                    .Must((x, d) => BookingDaysValid(d, x.Plan)).WithMessage("Invalid booking days");

                RuleFor(x => x.Plan.Days)
                    .Must((x, d) => DaysFollowLegs(d, x.Plan.Legs!.Count)).WithMessage(DaysFollowLegsMessage)
                    .When(x => x.Plan.Legs is { Count: > 0 });

                RuleForEach(x => x.Plan.Days)
                    .Must((x, d) => SameAsDayInLeg(x.Plan, d, d.Stay?.SameAsDay)
                                    && (d.StayShortlist == null || d.StayShortlist.All(i => i == null || SameAsDayInLeg(x.Plan, d, i.SameAsDay))))
                    .WithMessage(SameCityStayMessage);
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
            RuleFor(s => s.Rooms)
                .InclusiveBetween(1, 15).WithMessage("Rooms must be 1 to 15")
                .When(s => s.Rooms != null);
            RuleFor(s => s.Needs)
                .Must(n => n!.Length <= TripNeeds.All.Length
                           && n.All(k => k != null && TripNeeds.All.Contains(k))
                           && n.Distinct().Count() == n.Length)
                .WithMessage("Invalid needs")
                .When(s => s.Needs != null);
            RuleFor(s => s.PickupTime).Must(IsValidPickupTime).WithMessage(PickupTimeMessage);
            RuleFor(s => s.PickupPlace).Must(IsValidPickupPlace).WithMessage(PickupPlaceMessage);
            RuleFor(s => s.MaxDays)
                .InclusiveBetween(1, TripLimits.MaxDays).WithMessage(PlanTripRequestValidator.MaxDaysMessage)
                .When(s => s.MaxDays != null);
        }
    }

    // An app that does not know steps writes a "No taxi this day" day or a "No stay tonight" night back with an empty id and no skipped flag.
    internal const string StepsUnawareKey = "trip.stepsUnaware";

    // The body carried no legs (v3 or an app that does not know cities); its synthesised leg skips the leg text rules.
    internal const string LeglessKey = "trip.legless";

    protected override bool PreValidate(ValidationContext<SaveTripRequest> context, ValidationResult result)
    {
        var plan = context.InstanceToValidate?.Plan;
        context.RootContextData[StepsUnawareKey] = plan is { Steps: null };
        context.RootContextData[LeglessKey] = plan is { Legs: not { Count: > 0 } };
        plan?.Normalize();
        return true;
    }

    internal sealed class TripCityLegValidator : AbstractValidator<TripCityLeg>
    {
        public TripCityLegValidator()
        {
            RuleFor(l => l.CityName)
                .Must((l, n, ctx) => (ctx.RootContextData.TryGetValue(LeglessKey, out var v) && v is true)
                                     || (n != null && n.Trim().Length is >= 1 and <= MaxNameLength))
                .WithMessage("City name must be 1 to 120 characters");
            RuleFor(l => l)
                .Must(l => l.Latitude is >= -90 and <= 90 && l.Longitude is >= -180 and <= 180)
                .OverridePropertyName("Coordinates")
                .WithMessage(PlanTripRequestValidator.InvalidCoordinatesMessage);
            RuleFor(l => l.Arrival!).SetValidator(new TripTravelValidator()).When(l => l.Arrival != null);
        }
    }

    internal sealed class TripTravelValidator : AbstractValidator<TripTravel>
    {
        public TripTravelValidator()
        {
            RuleFor(t => t.Mode).Must(m => m != null && TripTravelModes.All.Contains(m)).WithMessage(TravelMessage);
            RuleFor(t => t.FromName).Must(n => n == null || n.Length <= MaxNameLength).WithMessage(TravelMessage);
            RuleFor(t => t.How).Must(h => h == null || TripTravelHows.All.Contains(h)).WithMessage(TravelMessage);
            RuleFor(t => t.OwnName)
                .Must(n => n == null || n.Trim().Length <= MaxOwnNameLength).WithMessage("Name or number can be up to 80 characters");
            RuleFor(t => t.OwnPhone).Must(IsValidPhone).WithMessage(PhoneMessage);
            RuleFor(t => t.LeavesAt).Must(IsValidPickupTime).WithMessage(TimeMessage);
            // A journey carries the real road minutes; the own sheet still offers only TripTravelRoad.All.
            RuleFor(t => t.RoadMinutes)
                .Must(m => m == null || m.Value is >= 0 and <= MaxTravelRoadMinutes).WithMessage(RoadTimeMessage);
            RuleFor(t => t.Note).Must(IsValidNote).WithMessage(NoteMessage);

            RuleFor(t => t)
                .Must(t => t.JourneyId is { } id && id != Guid.Empty
                           && t.How == null && t.OwnName == null && t.OwnPhone == null && t.LeavesAt == null)
                .OverridePropertyName("JourneyId")
                .WithMessage(TravelMessage)
                .When(t => t.Mode == TripTravelModes.Journey);
            RuleFor(t => t.JourneyId).Null().WithMessage(TravelMessage).When(t => t.Mode == TripTravelModes.Own);
            RuleFor(t => t.PlanKey).Must(k => k == null || TaxiWaysRules.IsKey(k)).WithMessage(TravelMessage);
            RuleFor(t => t.Cars).Must(c => c == null || c.Value is >= 1 and <= JourneyRules.MaxLegs).WithMessage(TravelMessage);
            RuleFor(t => t.ListedTotal).Must(v => v == null || v.Value is >= 0 and <= MaxTravelListedTotal).WithMessage(TravelMessage);
            RuleFor(t => t.HubName).Must(n => n == null || n.Trim().Length <= MaxNameLength).WithMessage(TravelMessage);
        }

        public const int MaxTravelRoadMinutes = 900;
        public const int MaxTravelListedTotal = 10_000_000;
    }

    internal sealed class TripTaxiChoiceValidator : AbstractValidator<TripTaxiChoice>
    {
        public TripTaxiChoiceValidator()
        {
            RuleFor(t => t.TaxiId)
                .Must((t, id, ctx) => t.IsOwn == true || t.Skipped == true || (id != null && id != Guid.Empty)
                                      || (id == null && ctx.RootContextData.TryGetValue(StepsUnawareKey, out var v) && v is true))
                .WithMessage("Taxi is required");
            RuleFor(t => t.Name).Must(IsValidOwnName).WithMessage(OwnNameMessage).When(t => t.IsOwn == true);
            RuleFor(t => t.IsOwn)
                .Must(own => own != true).WithMessage("A day is either your own taxi or no taxi")
                .When(t => t.Skipped == true);
            RuleFor(t => t.OwnPhone).Must(IsValidPhone).WithMessage(PhoneMessage);
            RuleFor(t => t.Note).Must(IsValidNote).WithMessage(NoteMessage);
            RuleFor(t => t.Booking!).SetValidator(new TripTaxiBookingValidator()).When(t => t.Booking != null);
        }
    }

    internal sealed class TripStepsValidator : AbstractValidator<TripSteps>
    {
        public const string StartFromMessage = "Starting from must be 1 to 80 characters";
        public const string StatusMessage = "Invalid step status";
        public const string DateMessage = "Invalid travel date";

        public TripStepsValidator()
        {
            RuleFor(s => s.StartFrom!.Name)
                .Must(n => n != null && n.Trim().Length is >= 1 and <= TripStepRules.NameMax).WithMessage(StartFromMessage)
                .When(s => s.StartFrom != null);
            RuleFor(s => s.StartFrom!)
                .Must(p => p.Lat is >= -90 and <= 90 && p.Lon is >= -180 and <= 180)
                .OverridePropertyName("Coordinates")
                .WithMessage(PlanTripRequestValidator.InvalidCoordinatesMessage)
                .When(s => s.StartFrom != null);
            RuleFor(s => s.GettingThere!).SetValidator(new TripStepValidator()).When(s => s.GettingThere != null);
            RuleFor(s => s.ComingBack!).SetValidator(new TripStepValidator()).When(s => s.ComingBack != null);
        }
    }

    internal sealed class TripStepValidator : AbstractValidator<TripStep>
    {
        public TripStepValidator()
        {
            RuleFor(s => s.Status)
                .Must(v => TripStepStatuses.All.Contains(v!)).WithMessage(TripStepsValidator.StatusMessage)
                .When(s => s.Status != null);
            RuleFor(s => s.Note).Must(IsValidNote).WithMessage(NoteMessage);
            RuleFor(s => s.Date)
                .Must(d => PlanTripRequestValidator.IsValidStartDate(d!.Value)).WithMessage(TripStepsValidator.DateMessage)
                .When(s => s.Date != null);
        }
    }

    // Days are checked against the plan by BookingDaysValid.
    internal sealed class TripTaxiBookingValidator : AbstractValidator<TripTaxiBooking>
    {
        public TripTaxiBookingValidator()
        {
            RuleFor(b => b.AgreedFare).InclusiveBetween(1, MaxPrice).WithMessage("Invalid fare");
            RuleFor(b => b.PickupTime).Must(IsValidPickupTime).WithMessage(PickupTimeMessage);
            RuleFor(b => b.PickupPlace).Must(IsValidPickupPlace).WithMessage(PickupPlaceMessage);
            RuleFor(b => b.Note).Must(IsValidNote).WithMessage(NoteMessage);
        }
    }

    internal sealed class TripStayChoiceValidator : AbstractValidator<TripStayChoice>
    {
        public TripStayChoiceValidator()
        {
            RuleFor(s => s.StayId)
                .Must((s, id, ctx) => s.IsOwn == true || s.Skipped == true || (id != null && id != Guid.Empty)
                                      || (id == null && ctx.RootContextData.TryGetValue(StepsUnawareKey, out var v) && v is true))
                .WithMessage("Stay is required");
            RuleFor(s => s.Name).Must(IsValidOwnName).WithMessage(OwnNameMessage).When(s => s.IsOwn == true);
            RuleFor(s => s.IsOwn)
                .Must(own => own != true).WithMessage("A night is either your own stay or no stay")
                .When(s => s.Skipped == true);
            RuleFor(s => s)
                .Must(s => IsValidCoordinates(s.Latitude, s.Longitude))
                .OverridePropertyName("Coordinates")
                .WithMessage(PlanTripRequestValidator.InvalidCoordinatesMessage);
            RuleFor(s => s.OwnPhone).Must(IsValidPhone).WithMessage(PhoneMessage);
            RuleFor(s => s.Note).Must(IsValidNote).WithMessage(NoteMessage);
        }
    }

    internal sealed class TripStayShortlistItemValidator : AbstractValidator<TripStayShortlistItem>
    {
        public TripStayShortlistItemValidator()
        {
            RuleFor(i => i.StayId).NotEqual(Guid.Empty).WithMessage("Stay is required");
            RuleFor(i => i.Name)
                .Must(n => n == null || n.Length <= MaxNameLength).WithMessage("Stay name can be up to 120 characters");
            RuleFor(i => i.PricePerNight)
                .InclusiveBetween(0, MaxPrice).WithMessage("Invalid stay price")
                .When(i => i.PricePerNight != null);
            RuleFor(i => i)
                .Must(i => IsValidCoordinates(i.Latitude, i.Longitude))
                .OverridePropertyName("Coordinates")
                .WithMessage(PlanTripRequestValidator.InvalidCoordinatesMessage);
            RuleFor(i => i.DistanceKm)
                .InclusiveBetween(0, 1000).WithMessage("Invalid distance")
                .When(i => i.DistanceKm != null);
        }
    }

    internal sealed class TripTaxiShortlistItemValidator : AbstractValidator<TripTaxiShortlistItem>
    {
        public TripTaxiShortlistItemValidator()
        {
            RuleFor(i => i.TaxiId).NotEqual(Guid.Empty).WithMessage("Taxi is required");
            RuleFor(i => i.Name)
                .Must(n => n == null || n.Length <= MaxNameLength).WithMessage("Taxi name can be up to 120 characters");
            RuleFor(i => i.VehicleTypeName)
                .Must(n => n == null || n.Length <= MaxNameLength).WithMessage("Vehicle type can be up to 120 characters");
            RuleFor(i => i.Seats)
                .Must(TaxiSeats.IsValid).WithMessage("Invalid seats")
                .When(i => i.Seats != null);
            RuleFor(i => i.LocalDayRate)
                .InclusiveBetween(0, MaxPrice).WithMessage("Invalid day rate")
                .When(i => i.LocalDayRate != null);
        }
    }
}
