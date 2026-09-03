using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateAppTabRequestValidator : AbstractValidator<UpdateAppTabRequest>
{
    public UpdateAppTabRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(30).When(x => x.DisplayName != null)
            .WithMessage("DisplayName must be 1-30 characters");
    }
}
