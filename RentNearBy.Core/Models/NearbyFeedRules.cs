using System.Globalization;
using System.Text;

namespace RentNearBy.Core.Models;

public static class NearbyRules
{
    public const double NearKm = 15, FeaturedMaxKm = 25;
    public const int MaxFeatured = 2, DefaultPageSize = 20, MaxPageSize = 40, TaxiTierPageSize = 5, TaxiStepKm = 5;
    public const double TaxiGridMeters = 2000;
    public const double TaxiGridDegrees = 0.018;

    // Metres to km, rounded UP to one decimal (never shows a closer stay than it is).
    public static double StayKm(double meters)
        => Math.Ceiling(Math.Round(Math.Max(0, meters) / 1000.0 * 10, 6)) / 10;

    public static int TaxiKm(double meters)
    {
        var km = Math.Max(0, meters) / 1000.0;
        return Math.Max(TaxiStepKm, (int)Math.Ceiling(Math.Round(km / TaxiStepKm, 6)) * TaxiStepKm);
    }

    public static bool IsNear(double km) => km <= NearKm;

    public static string LabelKey(double km, string? itemCity, string? centreCity)
    {
        if (km > NearKm) return "far";
        var same = !string.IsNullOrWhiteSpace(itemCity) && !string.IsNullOrWhiteSpace(centreCity)
            && string.Equals(itemCity.Trim(), centreCity.Trim(), StringComparison.OrdinalIgnoreCase);
        return same ? "inCity" : "near";
    }

    // Same maths as the LocationCoarse column: latitude on a 0.018 degree grid, longitude on the same ground width.
    public static (double Lat, double Lng) TaxiGridSnap(double lat, double lng)
    {
        var snappedLat = Math.Round(lat / TaxiGridDegrees) * TaxiGridDegrees;
        var cos = Math.Max(Math.Cos(snappedLat * Math.PI / 180.0), 0.01);
        var lngStep = TaxiGridDegrees / cos;
        return (snappedLat, Math.Round(lng / lngStep) * lngStep);
    }

    public static string LivesInLine(IReadOnlyList<string> states)
    {
        var list = states.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count switch
        {
            0 => string.Empty,
            1 => $"We are live in {list[0]}",
            _ => $"We are live in {string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}",
        };
    }

    public static bool ValidLatLng(double? lat, double? lng)
        => lat is { } la && lng is { } lo && double.IsFinite(la) && double.IsFinite(lo)
            && la is >= -90 and <= 90 && lo is >= -180 and <= 180;
}

public sealed record NearbyToken(double DistanceMeters, Guid Id)
{
    public string Encode()
    {
        var raw = DistanceMeters.ToString("R", CultureInfo.InvariantCulture) + "|" + Id.ToString("N");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? s, out NearbyToken? token)
    {
        token = null;
        if (string.IsNullOrWhiteSpace(s) || s.Length > 120) return false;
        try
        {
            var b64 = s.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var parts = raw.Split('|');
            if (parts.Length != 2) return false;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                || !double.IsFinite(d) || d < 0) return false;
            if (parts[1].Length != 32 || !Guid.TryParseExact(parts[1], "N", out var id)) return false;
            token = new NearbyToken(d, id);
            return true;
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }
}

public sealed record NearbyArgs(double Lat, double Lng, int PageSize, NearbyToken? After)
{
    public bool FirstPage => After == null;
}

public sealed record NearbyRow(Guid Id, double DistanceMeters);

public enum NearbyKind { Stay, Taxi, StayJob }

public sealed record NearbyFilter(Guid? StayTypeId = null, string? Meals = null, string? Category = null,
    Guid? DepartmentId = null, int? MinExperience = null, int? MaxExperience = null, int? MinSalary = null,
    IReadOnlyCollection<Guid>? ExcludeIds = null);

public sealed record NearbyCity(long CityKey, string Name, string StateName, string DistrictName, double Lat, double Lng, double DistanceKm);
