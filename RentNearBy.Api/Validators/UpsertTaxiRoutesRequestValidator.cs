using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Validators;

public class UpsertTaxiRoutesRequestValidator : AbstractValidator<UpsertTaxiRoutesRequest>
{
    public const string FareRangeMessage = "Fare must be between ₹100 and ₹99,00,000";

    public UpsertTaxiRoutesRequestValidator()
    {
        RuleFor(x => x.Routes)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Add at least one route")
            .Must(r => r!.Count >= 1).WithMessage("Add at least one route")
            .Must(r => r!.Count <= TaxiLimits.MaxRoutes).WithMessage($"You can add up to {TaxiLimits.MaxRoutes} routes")
            .WithErrorCode(TaxiErrorTypes.TaxiRouteLimit);

        // ChildRules skips null elements, so a null route must fail here or UpsertRoutes would dereference it.
        RuleFor(x => x.Routes)
            .Must(r => r!.All(i => i != null))
            .OverridePropertyName(nameof(TaxiRouteInput.FromName))
            .WithMessage("Pick a From place")
            .When(x => x.Routes != null);

        RuleForEach(x => x.Routes).ChildRules(route =>
        {
            route.RuleFor(r => r.FromName)
                .Cascade(CascadeMode.Stop)
                .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Pick a From place")
                .Must(v => v!.Trim().Length <= TaxiLimits.PlaceNameMax).WithMessage("From place name is too long");

            route.RuleFor(r => r.ToName)
                .Cascade(CascadeMode.Stop)
                .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Pick a To place")
                .Must(v => v!.Trim().Length <= TaxiLimits.PlaceNameMax).WithMessage("To place name is too long");

            route.RuleFor(r => r.FromLabel)
                .Must(v => v!.Trim().Length <= TaxiLimits.PlaceLabelMax).WithMessage("From place label is too long")
                .When(r => r.FromLabel != null);

            route.RuleFor(r => r.ToLabel)
                .Must(v => v!.Trim().Length <= TaxiLimits.PlaceLabelMax).WithMessage("To place label is too long")
                .When(r => r.ToLabel != null);

            route.RuleFor(r => r)
                .Must(r => r.FromLatitude is >= -90 and <= 90 && r.ToLatitude is >= -90 and <= 90
                    && r.FromLongitude is >= -180 and <= 180 && r.ToLongitude is >= -180 and <= 180)
                .OverridePropertyName("Coordinates")
                .WithMessage("Invalid coordinates");

            route.RuleFor(r => r.Fare)
                .InclusiveBetween(TaxiLimits.MinFare, TaxiLimits.MaxFare).WithMessage(FareRangeMessage);

            route.RuleFor(r => r.TripType)
                .Must(v => v != null && TaxiTripTypes.All.Contains(v.Trim()))
                .WithMessage("Pick One way or Round trip");

            route.RuleFor(r => r)
                .Must(r => TaxiGeo.DistanceMeters(
                    (double)r.FromLatitude, (double)r.FromLongitude, (double)r.ToLatitude, (double)r.ToLongitude)
                    >= TaxiLimits.MinEndpointDistanceMeters)
                .OverridePropertyName("Endpoints")
                .WithMessage("From and To must be different places");

            route.RuleFor(r => r)
                .Must(r => TaxiRouteCityPicks.ShapeError(r) == null)
                .OverridePropertyName("CityKeys")
                .WithMessage((_, r) => TaxiRouteCityPicks.ShapeError(r))
                .WithErrorCode(TaxiErrorTypes.TaxiRouteCityInvalid);
        }).When(x => x.Routes != null);

        // Declared after the per-route rules so a field error wins over the duplicate check.
        RuleFor(x => x.Routes)
            .Must(r => r!.Select(DuplicateKey).Distinct().Count() == r!.Count)
            .OverridePropertyName("Duplicate")
            .WithMessage(DuplicateMessage)
            .When(x => x.Routes != null && x.Routes.Count > 1 && x.Routes.All(i => i != null));
    }

    public const string DuplicateMessage = "This route is already added";

    // One duplicate key for the request rows and (FB-47) the stored hidden routes.
    public static (decimal, decimal, decimal, decimal, string) DuplicateKey(TaxiRouteInput r) =>
        Key(r.FromLatitude, r.FromLongitude, r.ToLatitude, r.ToLongitude, r.TripType);

    public static (decimal, decimal, decimal, decimal, string) Key(decimal fromLat, decimal fromLng, decimal toLat, decimal toLng, string? tripType) =>
        (Math.Round(fromLat, 3), Math.Round(fromLng, 3), Math.Round(toLat, 3), Math.Round(toLng, 3), tripType?.Trim() ?? string.Empty);
}
