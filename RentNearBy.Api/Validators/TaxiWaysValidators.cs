using System.Text.RegularExpressions;
using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

internal static class TaxiWaysRules
{
    public static readonly Regex KeyPattern = new(@"^[A-Za-z0-9:_.~-]{3,200}$", RegexOptions.CultureInvariant);
    public const string DateMessage = "Pick a date within the next year";
    public const string PeopleMessage = "People must be 1 to 12";
    public const string NameMessage = "Place name is up to 80 characters";
    public const string KeyMessage = "Invalid plan";
    public const string FareMessage = "Invalid fare";

    public static bool IsDate(DateOnly date) => JourneyRules.IsTravelDate(date, JourneyRules.TodayIst(DateTime.UtcNow));
    // A name is a label: blank is fine (the handler falls back), only a very long one is refused.
    public static bool IsName(string? s) => s == null || s.Trim().Length <= JourneyRules.NameMax;
    public static bool IsKey(string? s) => s != null && KeyPattern.IsMatch(s);
    public static bool IsLat(double v) => v is >= -90 and <= 90 && !double.IsNaN(v);
    public static bool IsLng(double v) => v is >= -180 and <= 180 && !double.IsNaN(v);
    public static bool IsFare(int? v) => v == null || v is >= TaxiLimits.MinFare and <= TaxiLimits.MaxFare;
}

public class SaveJourneyLegRequestValidator : AbstractValidator<SaveJourneyLegRequest>
{
    public SaveJourneyLegRequestValidator()
    {
        RuleFor(x => x.LegIndex).InclusiveBetween(1, JourneyRules.MaxLegs).WithMessage("Invalid leg");
        RuleFor(x => x.LegKey).Must(TaxiWaysRules.IsKey).WithMessage(TaxiWaysRules.KeyMessage);
        RuleFor(x => x.FromName).Must(TaxiWaysRules.IsName).WithMessage(TaxiWaysRules.NameMessage);
        RuleFor(x => x.ToName).Must(TaxiWaysRules.IsName).WithMessage(TaxiWaysRules.NameMessage);
        RuleFor(x => x.MeetingPlace).Must(s => s == null || s.Trim().Length <= JourneyRules.NameMax).WithMessage(TaxiWaysRules.NameMessage);
        RuleFor(x => x.FromLat).Must(TaxiWaysRules.IsLat).WithMessage("Invalid coordinates");
        RuleFor(x => x.ToLat).Must(TaxiWaysRules.IsLat).WithMessage("Invalid coordinates");
        RuleFor(x => x.FromLng).Must(TaxiWaysRules.IsLng).WithMessage("Invalid coordinates");
        RuleFor(x => x.ToLng).Must(TaxiWaysRules.IsLng).WithMessage("Invalid coordinates");
        RuleFor(x => x.DriverTaxiId).NotEmpty().WithMessage("Driver is required");
        RuleFor(x => x.CoverTaxiIds).Must(ids => ids == null
                || (ids.Count < JourneyRules.MaxPeople && ids.All(i => i != Guid.Empty) && ids.Distinct().Count() == ids.Count))
            .WithMessage("Invalid drivers");
    }
}

public class SaveJourneyRequestValidator : AbstractValidator<SaveJourneyRequest>
{
    public SaveJourneyRequestValidator()
    {
        RuleFor(x => x.FromName).Must(TaxiWaysRules.IsName).WithMessage(TaxiWaysRules.NameMessage);
        RuleFor(x => x.ToName).Must(TaxiWaysRules.IsName).WithMessage(TaxiWaysRules.NameMessage);
        RuleFor(x => x.FromLat).Must(TaxiWaysRules.IsLat).WithMessage("Invalid coordinates");
        RuleFor(x => x.ToLat).Must(TaxiWaysRules.IsLat).WithMessage("Invalid coordinates");
        RuleFor(x => x.FromLng).Must(TaxiWaysRules.IsLng).WithMessage("Invalid coordinates");
        RuleFor(x => x.ToLng).Must(TaxiWaysRules.IsLng).WithMessage("Invalid coordinates");
        RuleFor(x => x.Date).Must(TaxiWaysRules.IsDate).WithMessage(TaxiWaysRules.DateMessage);
        RuleFor(x => x.People).InclusiveBetween(1, JourneyRules.MaxPeople).WithMessage(TaxiWaysRules.PeopleMessage);
        RuleFor(x => x.PlanKey).Must(TaxiWaysRules.IsKey).WithMessage(TaxiWaysRules.KeyMessage);
        RuleFor(x => x.Legs).NotNull().Must(l => l != null && l.Count is >= 1 and <= JourneyRules.MaxLegs)
            .WithMessage("A journey has 1 to 3 cars");
        RuleFor(x => x.Legs).Must(l => l != null && l.All(x => x != null))
            .When(x => x.Legs != null)
            .WithMessage("Invalid legs");
        RuleFor(x => x.Legs).Must(l => l != null && l.Select(x => x.LegIndex).OrderBy(i => i).SequenceEqual(Enumerable.Range(1, l.Count)))
            .When(x => x.Legs != null && x.Legs.All(l => l != null) && x.Legs.Count is >= 1 and <= JourneyRules.MaxLegs)
            .WithMessage("Invalid legs");
        RuleForEach(x => x.Legs).SetValidator(new SaveJourneyLegRequestValidator())
            .When(x => x.Legs != null && x.Legs.All(l => l != null));
        RuleFor(x => x.TripId).Must(id => id != Guid.Empty).When(x => x.TripId != null).WithMessage("Invalid trip");
        RuleFor(x => x.LegIndex).Null().When(x => x.TripId == null).WithMessage(PickCityMessage);
        RuleFor(x => x.LegIndex).NotNull().When(x => x.TripId != null).WithMessage(PickCityMessage);
        RuleFor(x => x.LegIndex).InclusiveBetween(2, TripLimits.MaxLegs).When(x => x.LegIndex != null).WithMessage(UnknownCityMessage);
    }

    public const string PickCityMessage = "Pick the city you travel to";
    public const string UnknownCityMessage = "Unknown city";
}

public class ReplaceJourneyDriverRequestValidator : AbstractValidator<ReplaceJourneyDriverRequest>
{
    public ReplaceJourneyDriverRequestValidator()
    {
        RuleFor(x => x.TaxiId).NotEmpty().WithMessage("Driver is required");
    }
}

public class JourneyStatusRequestValidator : AbstractValidator<JourneyStatusRequest>
{
    public JourneyStatusRequestValidator()
    {
        RuleFor(x => x.Status).Equal(JourneyStatuses.Completed).WithMessage("Only Completed is allowed");
    }
}

public class AskLegRequestValidator : AbstractValidator<AskLegRequest>
{
    public AskLegRequestValidator()
    {
        RuleFor(x => x.LegIndex).InclusiveBetween(1, JourneyRules.MaxLegs).WithMessage("Unknown leg");
        RuleFor(x => x.LegKey).Must(TaxiWaysRules.IsKey).WithMessage(TaxiWaysRules.KeyMessage);
        RuleFor(x => x.FromName).Must(TaxiWaysRules.IsName).WithMessage(TaxiWaysRules.NameMessage);
        RuleFor(x => x.ToName).Must(TaxiWaysRules.IsName).WithMessage(TaxiWaysRules.NameMessage);
        RuleFor(x => x.ComesFrom).Must(s => s == null || s.Trim().Length <= JourneyRules.NameMax).WithMessage(TaxiWaysRules.NameMessage);
        RuleFor(x => x.CarOf).InclusiveBetween(1, JourneyRules.MaxLegs).WithMessage("Invalid cars");
        RuleFor(x => x.TaxiIds).NotNull().Must(ids => ids != null && ids.Distinct().Count() is >= 1 and <= JourneyRules.MaxAskPerLeg)
            .WithMessage(TripExecutionRules.PickMessage);
        RuleFor(x => x.TaxiIds).Must(ids => ids.All(i => i != Guid.Empty))
            .When(x => x.TaxiIds != null)
            .WithMessage("Invalid drivers");
    }
}

public class AskDriversRequestValidator : AbstractValidator<AskDriversRequest>
{
    public AskDriversRequestValidator()
    {
        RuleFor(x => x.JourneyId).NotEmpty().WithMessage("Journey is required");
        RuleFor(x => x.StepKey).Must(TripStepKeys.IsValid).When(x => x.StepKey != null).WithMessage("Invalid step");
        RuleFor(x => x.ClientRequestId).Must(id => id != Guid.Empty).When(x => x.ClientRequestId != null).WithMessage("Invalid request id");
        RuleFor(x => x.Date).Must(TaxiWaysRules.IsDate).WithMessage(TaxiWaysRules.DateMessage);
        RuleFor(x => x.People).InclusiveBetween(1, JourneyRules.MaxPeople).WithMessage(TaxiWaysRules.PeopleMessage);
        RuleFor(x => x.FromName).Must(TaxiWaysRules.IsName).WithMessage(TaxiWaysRules.NameMessage);
        RuleFor(x => x.ToName).Must(TaxiWaysRules.IsName).WithMessage(TaxiWaysRules.NameMessage);
        RuleFor(x => x.Legs).NotNull().Must(l => l != null && l.Count is >= 1 and <= JourneyRules.MaxLegs)
            .WithMessage("Ask 1 to 3 cars");
        RuleFor(x => x.Legs).Must(l => l.All(x => x != null))
            .When(x => x.Legs != null)
            .WithMessage("Unknown leg");
        RuleFor(x => x.Legs).Must(l => l.Select(x => x.LegIndex).Distinct().Count() == l.Count)
            .When(x => x.Legs != null && x.Legs.All(l => l != null))
            .WithMessage("Unknown leg");
        RuleFor(x => x.Legs).Must(l => l.Sum(x => x.TaxiIds?.Distinct().Count() ?? 0) <= JourneyRules.MaxAskTaxis)
            .When(x => x.Legs != null && x.Legs.All(l => l != null))
            .WithMessage(TripExecutionRules.PickMessage);
        RuleForEach(x => x.Legs).SetValidator(new AskLegRequestValidator())
            .When(x => x.Legs != null && x.Legs.All(l => l != null));
    }
}
