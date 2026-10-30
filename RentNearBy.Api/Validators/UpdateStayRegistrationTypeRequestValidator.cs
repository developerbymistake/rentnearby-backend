using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpdateStayRegistrationTypeRequestValidator : AbstractValidator<UpdateStayRegistrationTypeRequest>
{
    public UpdateStayRegistrationTypeRequestValidator()
    {
        RuleFor(x => x.Name)
            .Cascade(CascadeMode.Stop)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Name is required")
            .Must(n => n!.Trim().Length is >= 2 and <= StayRegistrationRules.TypeNameMax).WithMessage("Name must be 2 to 60 characters")
            .When(x => x.Name != null);

        RuleFor(x => x.HelpText)
            .MaximumLength(StayRegistrationRules.HelpTextMax).WithMessage("Help text must not exceed 120 characters")
            .When(x => x.HelpText != null);

        RuleFor(x => x.SortOrder)
            .GreaterThanOrEqualTo(0).WithMessage("Sort order must be 0 or more")
            .When(x => x.SortOrder != null);
    }
}
