using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

// Whether JobDepartmentId is required depends on the route kind, so the handler checks it.
public class UpsertJobLookupRequestValidator : AbstractValidator<UpsertJobLookupRequest>
{
    public UpsertJobLookupRequestValidator()
    {
        RuleFor(x => x.Name)
            .Cascade(CascadeMode.Stop)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Name is required")
            .MaximumLength(StayJobRules.LookupNameMax).WithMessage($"Name must be at most {StayJobRules.LookupNameMax} characters");

        RuleFor(x => x.SortOrder)
            .InclusiveBetween(0, 9999).WithMessage("Sort order must be 0 to 9999");
    }
}
