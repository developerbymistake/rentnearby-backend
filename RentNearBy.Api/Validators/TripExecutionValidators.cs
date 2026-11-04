using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public static class TripExecutionValidation
{
    public const int MaxDay = TripLimits.MaxDays - 1;
    public const string PhonePattern = @"^[6-9]\d{9}$";
    public const string PhoneMessage = "Enter a 10 digit phone";
}

public class CreateTripEnquiryRequestValidator : AbstractValidator<CreateTripEnquiryRequest>
{
    public CreateTripEnquiryRequestValidator()
    {
        RuleFor(x => x.Kind)
            .Must(k => TripEnquiryKinds.All.Contains(k))
            .WithMessage("Kind must be Stay or Taxi");

        RuleFor(x => x.Day)
            .InclusiveBetween(1, TripExecutionValidation.MaxDay)
            .When(x => x.Kind != TripEnquiryKinds.Taxi)
            .WithMessage($"Day must be 1 to {TripExecutionValidation.MaxDay}");

        RuleFor(x => x.Days)
            .Must(d => d != null && d.Count is >= 1 and <= TripLeadRules.MaxLeadDays)
            .WithMessage($"Pick 1 to {TripLeadRules.MaxLeadDays} days")
            .Must(d => d == null || d.Distinct().Count() == d.Count)
            .WithMessage("Days must be different")
            .Must(d => d == null || d.All(v => v is >= 1 and <= TripLimits.MaxDays))
            .WithMessage($"Day must be 1 to {TripLimits.MaxDays}")
            .When(x => x.Kind == TripEnquiryKinds.Taxi);

        RuleFor(x => x.PickupTime)
            .Matches(TripLeadRules.PickupTimePattern)
            .When(x => x.PickupTime != null)
            .WithMessage("Pickup time must be HH:mm");

        RuleFor(x => x.PickupPlace)
            .Must(v => v == null || v.Trim().Length <= TripLeadRules.PickupPlaceMax)
            .WithMessage($"Pickup place must be at most {TripLeadRules.PickupPlaceMax} characters");

        RuleFor(x => x.ListingIds)
            .NotNull()
            .Must(ids => ids.Count is >= 1 and <= TripExecutionRules.MaxOpenPerNight)
            .WithMessage($"Pick 1 to {TripExecutionRules.MaxOpenPerNight}")
            .Must(ids => ids.All(id => id != Guid.Empty))
            .WithMessage("Listing id must not be empty")
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("Listing ids must be different");
    }
}

public abstract class PhoneBookingFieldsValidator<T> : AbstractValidator<T> where T : IPhoneBookingFields
{
    protected PhoneBookingFieldsValidator()
    {
        RuleFor(x => x.HostName)
            .Must(v => !string.IsNullOrWhiteSpace(v) && v.Trim().Length <= TripExecutionRules.HostNameMax)
            .WithMessage($"Host name must be 1 to {TripExecutionRules.HostNameMax} characters");

        RuleFor(x => x.HostPhone)
            .NotNull()
            .Matches(TripExecutionValidation.PhonePattern)
            .WithMessage(TripExecutionValidation.PhoneMessage);

        RuleFor(x => x.RoomName)
            .Must(v => !string.IsNullOrWhiteSpace(v) && v.Trim().Length <= TripExecutionRules.RoomNameMax)
            .WithMessage($"Room must be 1 to {TripExecutionRules.RoomNameMax} characters");

        RuleFor(x => x.AgreedTotal)
            .InclusiveBetween(TripExecutionRules.MinAmount, TripExecutionRules.MaxAmount)
            .WithMessage("Agreed price must be 1 to 2000000");

        RuleFor(x => x.AdvanceAmount)
            .Must((x, amount) => amount == null || (amount >= 0 && amount <= x.AgreedTotal))
            .WithMessage("Advance must be 0 to the agreed price");

        RuleFor(x => x.AdvanceAmount)
            .Must(amount => amount is > 0)
            .When(x => x.AdvancePaid)
            .WithMessage("Enter the advance amount");

        RuleFor(x => x.Note)
            .Must(v => v == null || v.Length <= TripExecutionRules.NoteMax)
            .WithMessage($"Note must be at most {TripExecutionRules.NoteMax} characters");
    }
}

public class CreatePhoneBookingRequestValidator : PhoneBookingFieldsValidator<CreatePhoneBookingRequest>
{
    public CreatePhoneBookingRequestValidator()
    {
        RuleFor(x => x.StayId).NotEmpty().WithMessage("Stay must not be empty");
        RuleFor(x => x.Day)
            .InclusiveBetween(1, TripExecutionValidation.MaxDay)
            .WithMessage($"Day must be 1 to {TripExecutionValidation.MaxDay}");
        RuleFor(x => x.ReplaceEnquiryId)
            .Must(id => id == null || id != Guid.Empty)
            .WithMessage("Replace id must not be empty");
    }
}

public class EditPhoneBookingRequestValidator : PhoneBookingFieldsValidator<EditPhoneBookingRequest>
{
}

public class CancelTripEnquiryRequestValidator : AbstractValidator<CancelTripEnquiryRequest>
{
    public CancelTripEnquiryRequestValidator()
    {
        RuleFor(x => x.Reason)
            .Must(v => v == null || v.Length <= TripExecutionRules.CancellationMax)
            .WithMessage($"Reason must be at most {TripExecutionRules.CancellationMax} characters");
    }
}

public class AcceptTripOfferRequestValidator : AbstractValidator<AcceptTripOfferRequest>
{
    public AcceptTripOfferRequestValidator()
    {
        RuleFor(x => x.ReplaceEnquiryId)
            .Must(id => id == null || id != Guid.Empty)
            .WithMessage("Replace id must not be empty");
    }
}

public abstract class TripOfferFieldsValidator<T> : AbstractValidator<T> where T : ITripOfferFields
{
    protected TripOfferFieldsValidator()
    {
        RuleFor(x => x)
            .Must(x => x.CheckIn < x.CheckOut)
            .WithMessage("Check-out must be after check-in")
            .Must(x => x.CheckOut.DayNumber - x.CheckIn.DayNumber <= TripExecutionValidation.MaxDay)
            .WithMessage($"Nights must be at most {TripExecutionValidation.MaxDay}")
            .OverridePropertyName("CheckOut");

        RuleFor(x => x.Lines)
            .NotNull()
            .Must(l => l.Count is >= 1 and <= TripExecutionRules.MaxLinesPerOffer)
            .WithMessage($"Rooms must be 1 to {TripExecutionRules.MaxLinesPerOffer} lines");

        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.RoomName)
                .Must(v => v == null || (v.Trim().Length >= 1 && v.Trim().Length <= TripExecutionRules.RoomNameMax))
                .WithMessage($"Room must be 1 to {TripExecutionRules.RoomNameMax} characters");
            line.RuleFor(l => l.Units).InclusiveBetween(1, 10).WithMessage("Units must be 1 to 10");
            line.RuleFor(l => l.Guests).InclusiveBetween(1, 40).WithMessage("Guests must be 1 to 40");
            line.RuleFor(l => l.PricePerNight).InclusiveBetween(1, 200_000).WithMessage("Price per night must be 1 to 200000");
        });

        RuleFor(x => x.AdvanceAmount)
            .Must(v => v == null || v >= 0)
            .WithMessage("Advance must be 0 or more");
        RuleFor(x => x.BalanceNote)
            .Must(v => v == null || v.Length <= TripExecutionRules.BalanceNoteMax)
            .WithMessage($"Balance must be at most {TripExecutionRules.BalanceNoteMax} characters");
        RuleFor(x => x.CancellationText)
            .Must(v => v == null || v.Length <= TripExecutionRules.CancellationMax)
            .WithMessage($"Cancellation must be at most {TripExecutionRules.CancellationMax} characters");
        RuleFor(x => x.Inclusions)
            .Must(v => v == null || v.Length <= 200)
            .WithMessage("Inclusions must be at most 200 characters");
    }
}

public class SendTripOfferRequestValidator : TripOfferFieldsValidator<SendTripOfferRequest>
{
    public SendTripOfferRequestValidator()
    {
        RuleFor(x => x.NeedsEchoed)
            .Must(n => n == null || n.All(TripNeeds.All.Contains))
            .WithMessage("Needs must be no_stairs, kids_safe or veg_food");
        RuleFor(x => x.CounterOfferId)
            .Must(id => id == null || id != Guid.Empty)
            .WithMessage("Counter id must not be empty");
    }
}

public class RevisedOfferRequestValidator : TripOfferFieldsValidator<RevisedOfferRequest>
{
    public RevisedOfferRequestValidator()
    {
        RuleFor(x => x.Reason)
            .Must(v => !string.IsNullOrWhiteSpace(v) && v.Trim().Length <= TripOfferRules.ReasonMax)
            .WithMessage($"Reason must be 1 to {TripOfferRules.ReasonMax} characters");
        RuleFor(x => x.ChangeRequestId)
            .Must(id => id == null || id != Guid.Empty)
            .WithMessage("Change request id must not be empty");
    }
}

public class CounterRequestValidator : AbstractValidator<CounterRequest>
{
    public CounterRequestValidator()
    {
        RuleFor(x => x.PricePerNight)
            .InclusiveBetween(TripOfferRules.CounterMinPerNight, TripOfferRules.CounterMaxPerNight)
            .WithMessage("Price per night must be between Rs 100 and Rs 2,00,000");
        RuleFor(x => x.Note)
            .Must(v => v == null || v.Length <= TripExecutionRules.NoteMax)
            .WithMessage($"Note must be at most {TripExecutionRules.NoteMax} characters");
    }
}

public class ChangeRequestRequestValidator : AbstractValidator<ChangeRequestRequest>
{
    public ChangeRequestRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => x.CheckIn < x.CheckOut)
            .WithMessage("Check-out must be after check-in")
            .Must(x => x.CheckOut.DayNumber - x.CheckIn.DayNumber <= TripExecutionValidation.MaxDay)
            .WithMessage($"Nights must be at most {TripExecutionValidation.MaxDay}")
            .OverridePropertyName("CheckOut");
        RuleFor(x => x.Adults).InclusiveBetween(1, 30).WithMessage("Adults must be 1 to 30");
        RuleFor(x => x.Kids).InclusiveBetween(0, 20).WithMessage("Kids must be 0 to 20");
        RuleFor(x => x.Rooms).InclusiveBetween(1, 15).WithMessage("Rooms must be 1 to 15");
        RuleFor(x => x.Note)
            .Must(v => v == null || v.Length <= TripExecutionRules.NoteMax)
            .WithMessage($"Note must be at most {TripExecutionRules.NoteMax} characters");
    }
}

public class AskAgainRequestValidator : AbstractValidator<AskAgainRequest>
{
    public AskAgainRequestValidator()
    {
        RuleFor(x => x.Kind)
            .Must(k => TripEnquiryKinds.All.Contains(k))
            .WithMessage("Kind must be Stay or Taxi");
        RuleFor(x => x.Day)
            .InclusiveBetween(1, TripLimits.MaxDays)
            .WithMessage($"Day must be 1 to {TripLimits.MaxDays}");
    }
}

public class CreateTaxiBookingRequestValidator : AbstractValidator<CreateTaxiBookingRequest>
{
    public CreateTaxiBookingRequestValidator()
    {
        RuleFor(x => x.TaxiId).NotEmpty().WithMessage("Taxi must not be empty");

        RuleFor(x => x.Days)
            .Must(d => d != null && d.Count is >= 1 and <= TripLimits.MaxDays)
            .WithMessage($"Pick 1 to {TripLimits.MaxDays} days")
            .Must(d => d == null || d.Distinct().Count() == d.Count)
            .WithMessage("Days must be different")
            .Must(d => d == null || d.All(v => v is >= 1 and <= TripLimits.MaxDays))
            .WithMessage($"Day must be 1 to {TripLimits.MaxDays}");

        RuleFor(x => x.AgreedFare)
            .InclusiveBetween(TaxiLimits.MinFare, TaxiLimits.MaxFare)
            .WithMessage("Fare must be between Rs 100 and Rs 99,00,000");

        RuleFor(x => x.PickupTime)
            .NotNull()
            .Matches(TripLeadRules.PickupTimePattern)
            .WithMessage("Pickup time must be HH:mm");

        RuleFor(x => x.PickupPlace)
            .Must(v => !string.IsNullOrWhiteSpace(v) && v.Trim().Length <= TripLeadRules.PickupPlaceMax)
            .WithMessage($"Pickup place must be 1 to {TripLeadRules.PickupPlaceMax} characters");

        RuleFor(x => x.Note)
            .Must(v => v == null || v.Length <= TripExecutionRules.NoteMax)
            .WithMessage($"Note must be at most {TripExecutionRules.NoteMax} characters");

        RuleFor(x => x.ReplaceEnquiryId)
            .Must(id => id == null || id != Guid.Empty)
            .WithMessage("Replace id must not be empty");
    }
}

public class AskAdvanceRequestValidator : AbstractValidator<AskAdvanceRequest>
{
    public AskAdvanceRequestValidator()
    {
        RuleFor(x => x.Amount)
            .InclusiveBetween(TripExecutionRules.MinAmount, TripExecutionRules.MaxAmount)
            .WithMessage("Advance must be 1 to 2000000");

        RuleFor(x => x.HoldUntil)
            .Must(d => d.Kind != DateTimeKind.Unspecified)
            .WithMessage("Hold time must carry a time zone");

        RuleFor(x => x.UpiId)
            .Must(TripAdvanceRules.IsValidUpi)
            .WithMessage("Enter a UPI ID like name@bank");

        RuleFor(x => x.WhatsAppNumber)
            .NotNull()
            .Matches(TripExecutionValidation.PhonePattern)
            .WithMessage(TripExecutionValidation.PhoneMessage);
    }
}

public class DriverFareRequestValidator : AbstractValidator<DriverFareRequest>
{
    public DriverFareRequestValidator()
    {
        RuleFor(x => x.Fare)
            .InclusiveBetween(TaxiLimits.MinFare, TaxiLimits.MaxFare)
            .WithMessage("Fare must be between Rs 100 and Rs 99,00,000");
        RuleFor(x => x.Note)
            .Must(v => v == null || v.Length <= TripExecutionRules.NoteMax)
            .WithMessage($"Note must be at most {TripExecutionRules.NoteMax} characters");
    }
}

public class TripDriverListQueryValidator : AbstractValidator<TripDriverListQuery>
{
    public TripDriverListQueryValidator()
    {
        RuleFor(x => x.Status)
            .Must(s => TripDriverBuckets.All.Contains(s))
            .WithMessage("Status must be new, replied, booked or closed");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("Page must be 1 or more");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 50).WithMessage("Page size must be 1 to 50");
    }
}

public class TripOwnerListQueryValidator : AbstractValidator<TripOwnerListQuery>
{
    public TripOwnerListQueryValidator()
    {
        RuleFor(x => x.Status)
            .Must(s => TripOwnerBuckets.All.Contains(s))
            .WithMessage("Status must be new, offered, confirmed or closed");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("Page must be 1 or more");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 50).WithMessage("Page size must be 1 to 50");
    }
}
