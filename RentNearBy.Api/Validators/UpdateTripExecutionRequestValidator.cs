using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateTripExecutionRequestValidator : AbstractValidator<UpdateTripExecutionRequest>
{
    public UpdateTripExecutionRequestValidator()
    {
        RuleFor(x => x.IsEnabled).NotNull().WithMessage("IsEnabled is required");
        RuleFor(x => x.Reason).MaximumLength(500).When(x => x.Reason != null)
            .WithMessage("Reason must be at most 500 characters");
    }
}
