using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class SpotMinutesRequestValidator : AbstractValidator<SpotMinutesRequest>
{
    public const string NoIdsMessage = "Pick at least one place";
    public const string TooManyIdsMessage = "Up to 25 places";
    public const string InvalidIdMessage = "Invalid place";
    public const int MaxIdLength = 100;

    public SpotMinutesRequestValidator()
    {
        RuleFor(x => x.SpotIds)
            .Must(ids => ids is { Count: > 0 }).WithMessage(NoIdsMessage)
            .Must(ids => ids == null || ids.Count <= TripSpotMinutes.MaxIds).WithMessage(TooManyIdsMessage)
            .Must(ids => ids == null || ids.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= MaxIdLength)).WithMessage(InvalidIdMessage);
    }
}
