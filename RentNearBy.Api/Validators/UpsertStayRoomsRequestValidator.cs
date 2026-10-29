using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpsertStayRoomsRequestValidator : AbstractValidator<UpsertStayRoomsRequest>
{
    public UpsertStayRoomsRequestValidator()
    {
        RuleFor(x => x.Rooms)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Add at least one room")
            .Must(r => r!.Count >= 1).WithMessage("Add at least one room")
            .Must(r => r!.Count <= StayLimits.MaxRooms).WithMessage($"A stay can have at most {StayLimits.MaxRooms} rooms");

        // ChildRules skips null elements, so a null room must fail here or UpsertRooms would dereference it.
        RuleFor(x => x.Rooms)
            .Must(r => r!.All(i => i != null))
            .OverridePropertyName(nameof(StayRoomInput.RoomName))
            .WithMessage("Pick a room name")
            .When(x => x.Rooms != null);

        RuleForEach(x => x.Rooms).ChildRules(room =>
        {
            room.RuleFor(r => r.RoomName)
                .Must(v => v != null && StayRoomNames.All.Contains(v))
                .WithMessage("Pick a room name");

            room.RuleFor(r => r.PricePerNight)
                .InclusiveBetween(StayLimits.MinPrice, StayLimits.MaxPrice)
                .WithMessage("Price per night must be between ₹100 and ₹99,00,000");

            room.RuleFor(r => r.MaxGuests)
                .InclusiveBetween((short)1, (short)4).WithMessage("Max guests must be 1, 2, 3 or 4+");

            room.RuleFor(r => r.Meals)
                .Must(v => v != null && StayMeals.All.Contains(v))
                .WithMessage("Meals must be Room only, Breakfast or All meals");
        }).When(x => x.Rooms != null);

        // Declared after the per-room rules so "Pick a room name" wins; OverridePropertyName keeps it
        // off the "Rooms" property that UpsertRooms maps to StayRoomLimit.
        RuleFor(x => x.Rooms)
            .Must(r => r!.Select(i => i?.RoomName).Distinct().Count() == r!.Count)
            .OverridePropertyName(nameof(StayRoomInput.RoomName))
            .WithMessage("Each room needs a different name")
            .When(x => x.Rooms != null && x.Rooms.Count > 1);
    }
}
