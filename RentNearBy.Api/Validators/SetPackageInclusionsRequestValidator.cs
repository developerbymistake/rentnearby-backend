using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class SetPackageInclusionsRequestValidator : AbstractValidator<SetPackageInclusionsRequest>
{
    public SetPackageInclusionsRequestValidator()
    {
        // An empty list is a legitimate full-replace ("clear all inclusions") — only reject a null list.
        RuleFor(x => x.InclusionIds).NotNull().WithMessage("InclusionIds must be provided (an empty list clears all inclusions)");
    }
}
