using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class CreateRoomTypeRequestValidator : AbstractValidator<CreateRoomTypeRequest>
{
    public CreateRoomTypeRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Room type name is required")
            .MaximumLength(50).WithMessage("Room type name must not exceed 50 characters");
    }
}
