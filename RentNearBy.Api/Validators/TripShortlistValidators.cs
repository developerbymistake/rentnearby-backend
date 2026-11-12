using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class AddShortlistRequestValidator : AbstractValidator<AddShortlistRequest>
{
    public const int NameMax = 120;

    public AddShortlistRequestValidator()
    {
        RuleFor(x => x.Kind).Must(k => TripShortlistKinds.All.Contains(k)).WithMessage("Kind must be Stay, Taxi or Car");
        RuleFor(x => x.Name).Must(n => !string.IsNullOrWhiteSpace(n) && n.Trim().Length <= NameMax)
            .WithMessage($"Name must be 1 to {NameMax} characters");
        RuleFor(x => x.LegIndex).InclusiveBetween(1, JourneyRules.MaxLegs).When(x => x.LegIndex != null).WithMessage("Unknown leg");
        RuleFor(x => x).Must(x => x.JourneyId == null || x.JourneyId != Guid.Empty).WithMessage("Invalid journey");
    }
}

public class CloseShortlistRequestValidator : AbstractValidator<CloseShortlistRequest>
{
    public CloseShortlistRequestValidator()
    {
        RuleFor(x => x.Reason).Must(r => TripShortlistClosedReasons.ClientClose.Contains(r)).WithMessage("Reason must be Own or Skipped");
    }
}

public class MarkBookedRequestValidator : AbstractValidator<MarkBookedRequest>
{
    public MarkBookedRequestValidator()
    {
        RuleFor(x => x.ListingId).Must(id => id != Guid.Empty).When(x => x.ListingId != null).WithMessage("Invalid listing");
    }
}
