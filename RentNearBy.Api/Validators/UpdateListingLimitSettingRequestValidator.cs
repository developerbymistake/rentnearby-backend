using FluentValidation;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Api.Validators;

public class UpdateListingLimitSettingRequestValidator : AbstractValidator<UpdateListingLimitSettingRequest>
{
    public UpdateListingLimitSettingRequestValidator()
    {
        RuleFor(x => x.MaxListings).GreaterThan(0).WithMessage("MaxListings must be greater than 0");
    }
}
