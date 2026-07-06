using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class ResolveReportRequestValidator : AbstractValidator<ResolveReportRequest>
{
    private static readonly string[] AllowedActions =
    [
        "PostDeactivated", "PostDeleted", "AccountDeactivated", "Resolved"
    ];

    public ResolveReportRequestValidator()
    {
        RuleFor(x => x.ResolutionAction)
            .NotEmpty().WithMessage("A resolution action is required");

        RuleFor(x => x.ResolutionAction)
            .Must(a => AllowedActions.Contains(a)).WithMessage("Invalid resolution action")
            .When(x => !string.IsNullOrEmpty(x.ResolutionAction));
    }
}
