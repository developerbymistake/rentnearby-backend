using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpsertStayBasicRequestValidator : AbstractValidator<UpsertStayBasicRequest>
{
    public UpsertStayBasicRequestValidator()
    {
        RuleFor(x => x.Name)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Stay name is required")
            .Must(n => n!.Trim().Length is >= 2 and <= 80).WithMessage("Stay name must be 2 to 80 characters");

        RuleFor(x => x.StayTypeId)
            .NotEmpty().WithMessage("Stay type is required");

        RuleFor(x => x.VehicleAccess)
            .Must(v => v != null && StayVehicleAccess.All.Contains(v))
            .WithMessage("Vehicle access must be Car to gate, Short walk or Long walk");

        RuleFor(x => x.HostLivesHere)
            .NotNull().WithMessage("Tell guests if the host lives here");

        RuleFor(x => x.Facilities)
            .Must(f => f!.Count <= StayFacilities.All.Length).WithMessage("Too many facilities")
            .Must(f => f!.All(v => StayFacilities.All.Contains(v))).WithMessage("Unknown facility")
            .When(x => x.Facilities != null);

        RuleFor(x => x.Description)
            .MaximumLength(300).WithMessage("Description must not exceed 300 characters")
            .When(x => x.Description != null);

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90, 90).WithMessage("Invalid latitude");

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180, 180).WithMessage("Invalid longitude");

        RuleFor(x => x.Address)
            .NotEmpty().WithMessage("Address is required")
            .MaximumLength(500).WithMessage("Address must not exceed 500 characters");

        RuleFor(x => x.DistrictId)
            .NotEmpty().WithMessage("District is required");
    }
}
