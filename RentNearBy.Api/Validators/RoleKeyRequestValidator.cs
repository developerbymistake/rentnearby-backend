using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class RoleKeyRequestValidator : AbstractValidator<RoleKeyRequest>
{
    public RoleKeyRequestValidator()
    {
        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("role is required.");
    }
}
