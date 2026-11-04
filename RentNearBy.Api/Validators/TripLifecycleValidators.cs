using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class TripProblemRequestValidator : AbstractValidator<TripProblemRequest>
{
    public const int NoteMax = 300;

    public TripProblemRequestValidator()
    {
        RuleFor(x => x.Day)
            .InclusiveBetween(1, TripLimits.MaxDays)
            .WithMessage($"Day must be 1 to {TripLimits.MaxDays}");

        RuleFor(x => x.Kind)
            .Must(k => TripProblemKinds.All.Contains(k))
            .WithMessage("Kind must be DriverNoShow, HostNoRoom, RoadClosed or Other");

        RuleFor(x => x.EnquiryId)
            .NotEqual(Guid.Empty)
            .When(x => x.EnquiryId != null)
            .WithMessage("Enquiry id must not be empty");

        RuleFor(x => x.Note)
            .MaximumLength(NoteMax)
            .WithMessage($"Note must be at most {NoteMax} characters");

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90.0, 90.0)
            .When(x => x.Latitude != null)
            .WithMessage("Latitude must be -90 to 90");

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180.0, 180.0)
            .When(x => x.Longitude != null)
            .WithMessage("Longitude must be -180 to 180");
    }
}

public class ReportTravellerRequestValidator : AbstractValidator<ReportTravellerRequest>
{
    public ReportTravellerRequestValidator()
    {
        RuleFor(x => x.ReasonId)
            .NotEmpty().WithMessage("A reason is required");

        RuleFor(x => x.Details)
            .MaximumLength(500).WithMessage("Details must not exceed 500 characters");
    }
}
