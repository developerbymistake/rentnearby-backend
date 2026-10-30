using System.Text.RegularExpressions;
using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpsertTaxiBasicRequestValidator : AbstractValidator<UpsertTaxiBasicRequest>
{
    private static readonly Regex Mobile = new(@"^[6-9]\d{9}$");

    public UpsertTaxiBasicRequestValidator()
    {
        RuleFor(x => x.VehicleTypeId)
            .NotEmpty().WithMessage("Pick a vehicle type");

        RuleFor(x => x.Seats)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Pick the number of seats")
            .Must(TaxiSeats.IsValid).WithMessage("Seats must be 4, 6, 7 or 12+");

        RuleFor(x => x.NumberPlate)
            .Cascade(CascadeMode.Stop)
            .Must(p => !string.IsNullOrWhiteSpace(p)).WithMessage("Number plate is required")
            .Must(p => TaxiPlate.IsValid(TaxiPlate.Normalize(p))).WithMessage(TaxiPlate.InvalidMessage);

        RuleFor(x => x.ContactPhone)
            .Cascade(CascadeMode.Stop)
            .Must(p => !string.IsNullOrWhiteSpace(p)).WithMessage("Phone number is required")
            .Must(p => Mobile.IsMatch(p!.Trim())).WithMessage("Enter a valid 10-digit mobile number");

        RuleFor(x => x.WhatsAppNumber)
            .Cascade(CascadeMode.Stop)
            .Must(p => !string.IsNullOrWhiteSpace(p)).WithMessage("WhatsApp number is required")
            .Must(p => Mobile.IsMatch(p!.Trim())).WithMessage("Enter a valid 10-digit WhatsApp number");

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90, 90).WithMessage("Invalid latitude");

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180, 180).WithMessage("Invalid longitude");

        RuleFor(x => x.DistrictId)
            .NotEmpty().WithMessage("District is required");

        // null = keep the stored value; the handler requires them when nothing is stored.
        RuleFor(x => x.DrivingLicenceNumber)
            .Must(v => TaxiIdentityRules.IsValidDl(TaxiIdentityRules.NormalizeDl(v), TaxiIdentityRules.TodayIst().Year))
            .WithMessage(TaxiIdentityRules.DlInvalidMessage)
            .When(x => x.DrivingLicenceNumber != null);

        RuleFor(x => x.DateOfBirth)
            .Cascade(CascadeMode.Stop)
            .Must(v => TaxiIdentityRules.TryParseDob(v, out _)).WithMessage(TaxiIdentityRules.DobInvalidMessage)
            .Must(v => DobStatusOf(v) is not (DobStatus.Future or DobStatus.TooOld)).WithMessage(TaxiIdentityRules.DobInvalidMessage)
            .Must(v => DobStatusOf(v) != DobStatus.Minor).WithMessage(TaxiIdentityRules.DobMinorMessage)
            .When(x => x.DateOfBirth != null);
    }

    private static DobStatus DobStatusOf(string? value)
    {
        TaxiIdentityRules.TryParseDob(value, out var dob);
        return TaxiIdentityRules.GetDobStatus(dob, TaxiIdentityRules.TodayIst());
    }
}
