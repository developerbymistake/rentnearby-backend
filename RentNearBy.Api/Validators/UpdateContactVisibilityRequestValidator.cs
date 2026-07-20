using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateContactVisibilityRequestValidator : AbstractValidator<UpdateContactVisibilityRequest>
{
    public UpdateContactVisibilityRequestValidator()
    {
        RuleFor(x => x.IsContactVisible)
            .NotNull().WithMessage("isContactVisible is required.");
    }
}
