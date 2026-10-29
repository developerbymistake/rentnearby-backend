using System.Text.RegularExpressions;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

public static class TaxiStatuses
{
    public const string Draft = "Draft";
    public const string PendingReview = "PendingReview";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    public static readonly string[] All = [Draft, PendingReview, Approved, Rejected];
}

public static class TaxiTripTypes
{
    public const string OneWay = "OneWay";
    public const string RoundTrip = "RoundTrip";

    public static readonly string[] All = [OneWay, RoundTrip];
}

// 12 means "12+".
public static class TaxiSeats
{
    public static readonly short[] All = [4, 6, 7, 12];

    public static bool IsValid(short? seats) => seats.HasValue && All.Contains(seats.Value);
}

public static class TaxiVehicleCategories
{
    public const string Hatchback = "Hatchback";
    public const string Sedan = "Sedan";
    public const string Suv = "SUV";
    public const string Muv = "MUV";
    public const string TempoTraveller = "TempoTraveller";
    public const string Bus = "Bus";

    public static readonly string[] All = [Hatchback, Sedan, Suv, Muv, TempoTraveller, Bus];

    public static int SortOf(string category)
    {
        var index = Array.IndexOf(All, category);
        return index < 0 ? All.Length : index;
    }
}

public static class TaxiLimits
{
    public const int MaxRoutes = 10;
    public const int MinFare = 100;
    public const int MaxFare = 9_900_000;
    public const double MinEndpointDistanceMeters = 500;
    public const double MatchRadiusMeters = 5000;
    public const int PlaceNameMax = 80;
    public const int PlaceLabelMax = 160;
    public const int VehicleTypeNameMax = 60;
    public const int PhoneLength = 10;
}

// Step flags and the setup percentage, always computed from the row (Routes must be loaded).
public static class TaxiSetupRules
{
    public const int Step1Weight = 50;
    public const int RoutesWeight = 40;
    public const int SubmitWeight = 10;

    public static bool Step1Done(TaxiListing t) =>
        t.VehicleTypeId != null && t.Seats != null && t.NumberPlate != null && t.PlatePhotoUrl != null
        && t.ContactPhone != null && t.WhatsAppNumber != null
        && t.Latitude != null && t.Longitude != null && t.DistrictId != null;

    public static bool RoutesDone(TaxiListing t) => t.Routes.Count >= 1;

    public static bool Submitted(TaxiListing t) =>
        t.Status is TaxiStatuses.PendingReview or TaxiStatuses.Approved;

    public static int Percent(TaxiListing t) =>
        (Step1Done(t) ? Step1Weight : 0)
        + (RoutesDone(t) ? RoutesWeight : 0)
        + (Submitted(t) ? SubmitWeight : 0);
}

// Stored normalised (no spaces or hyphens, upper case); apps show Format().
public static class TaxiPlate
{
    public const int MaxLength = 12;
    public const string InvalidMessage = "Enter a valid number plate, e.g. UK 07 TA 1234";

    private static readonly Regex Standard = new(@"^([A-Z]{2})([0-9]{1,2})([A-Z]{1,3})([0-9]{4})$");
    private static readonly Regex Bharat = new(@"^([0-9]{2})(BH)([0-9]{4})([A-Z]{1,2})$");

    public static string Normalize(string? plate) =>
        (plate ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", string.Empty).Replace("-", string.Empty);

    public static bool IsValid(string? normalised) =>
        !string.IsNullOrEmpty(normalised) && normalised.Length <= MaxLength
        && (Standard.IsMatch(normalised) || Bharat.IsMatch(normalised));

    public static string? Format(string? normalised)
    {
        if (string.IsNullOrEmpty(normalised)) return null;
        var m = Standard.Match(normalised);
        if (!m.Success) m = Bharat.Match(normalised);
        if (!m.Success) return normalised;
        return string.Join(' ', Enumerable.Range(1, m.Groups.Count - 1).Select(i => m.Groups[i].Value));
    }
}

public static class TaxiErrorTypes
{
    public const string TaxiStepLocked = "TaxiStepLocked";
    public const string TaxiLocked = "TaxiLocked";
    public const string TaxiAlreadyExists = "TaxiAlreadyExists";
    public const string TaxiRouteLimit = "TaxiRouteLimit";
    public const string TaxiNotApproved = "TaxiNotApproved";
    public const string TaxiPlateTaken = "TaxiPlateTaken";
    public const string TaxiVehicleTypeInUse = "TaxiVehicleTypeInUse";
    public const string TaxiBaseOutsideDistrict = "TaxiBaseOutsideDistrict";
}

// Seeded rows are re-inserted on start when missing, so they can only be switched off, never deleted.
public static class TaxiVehicleTypeSeed
{
    private static readonly Regex SeededId = new(@"^a1000000-0000-0000-0000-0000000000[0-9]{2}$");

    public static bool IsSeeded(Guid id) => SeededId.IsMatch(id.ToString());
}

public static class TaxiGeo
{
    public static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6_371_000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
