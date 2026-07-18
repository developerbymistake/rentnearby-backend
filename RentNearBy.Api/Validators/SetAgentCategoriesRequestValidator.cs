using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class SetAgentCategoriesRequestValidator : AbstractValidator<SetAgentCategoriesRequest>
{
    public SetAgentCategoriesRequestValidator()
    {
        // An empty list is a legitimate full-replace ("unassign from all categories") — only reject a null list.
        RuleFor(x => x.ServiceCategoryIds).NotNull().WithMessage("ServiceCategoryIds must be provided (an empty list unassigns from all categories)");
    }
}
