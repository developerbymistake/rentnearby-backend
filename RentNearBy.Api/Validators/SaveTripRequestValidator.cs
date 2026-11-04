using System.Text.RegularExpressions;
using FluentValidation;
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

    internal static bool IsValidCoordinates(double? lat, double? lon)
        => (lat == null && lon == null)
           || (lat is >= -90 and <= 90 && lon is >= -180 and <= 180);

    // A booking copy must list distinct days of this plan, including the day it sits on.
    internal static bool BookingDaysValid(TripDay day, int dayCount)
    {
        var booking = day.Taxi?.Booking;
        if (booking == null) return true;
        var days = booking.Days;
        if (days == null || days.Length == 0) return false;
        return days.Distinct().Count() == days.Length
               && days.All(n => n >= 1 && n <= dayCount)
               && days.Contains(day.Day);
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
                    .Must((x, d) => BookingDaysValid(d, x.Plan.Days.Count)).WithMessage("Invalid booking days");
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
        }
    }

    internal sealed class TripTaxiChoiceValidator : AbstractValidator<TripTaxiChoice>
    {
        public TripTaxiChoiceValidator()
        {
            RuleFor(t => t.TaxiId)
                .Must((t, id) => t.IsOwn == true || (id != null && id != Guid.Empty)).WithMessage("Taxi is required");
            RuleFor(t => t.OwnPhone).Must(IsValidPhone).WithMessage(PhoneMessage);
            RuleFor(t => t.Note).Must(IsValidNote).WithMessage(NoteMessage);
            RuleFor(t => t.Booking!).SetValidator(new TripTaxiBookingValidator()).When(t => t.Booking != null);
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
                .Must((s, id) => s.IsOwn == true || (id != null && id != Guid.Empty)).WithMessage("Stay is required");
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
