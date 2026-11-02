namespace RentNearBy.Core.Models;

// Config section "TripPlanner"; env TRIP_PLANNER_ENABLED overrides Enabled.
public sealed class TripPlannerOptions
{
    public const string Section = "TripPlanner";

    public bool Enabled { get; set; } = true;
    public int DayLimitMinutes { get; set; } = 450;
    public int SpotsPageSize { get; set; } = 20;
    public int MaxSpotsPerTrip { get; set; } = 40;
    public int MaxDays { get; set; } = 14;
    public int CacheTtlHours { get; set; } = 48;
    public int StaleTtlDays { get; set; } = 7;
    public int NotReadyTtlMinutes { get; set; } = 60;
    public int LockSeconds { get; set; } = 30;
    public double FallbackSpeedKmh { get; set; } = 30;
    public double EstimateFactor { get; set; } = 2.0;
    public int RouteGeometryMaxDays { get; set; } = 14;
    public int RouteCacheTtlDays { get; set; } = 7;
    // A city with more spots than this skips the cached matrix and uses the direct table.
    public int CityMatrixMaxSpots { get; set; } = 300;
    public int CityCatalogTtlHours { get; set; } = 6;
    public int CityCatalogStaleTtlDays { get; set; } = 7;
    public int CityCatalogRevalidateSeconds { get; set; } = 300;
    // Atlas clamps to 200.
    public int CityCatalogPageSize { get; set; } = 200;
    public int CityCatalogMaxPages { get; set; } = 50;
    // Default page of GET /trips/cities.
    public int CitiesPageSize { get; set; } = 100;
}

// Config section "Atlas"; env ATLAS_BASE_URL / ATLAS_API_KEY fill empty values.
public sealed class AtlasOptions
{
    public const string Section = "Atlas";

    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 15;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

// Config section "Osrm"; env OSRM_URL fills an empty BaseUrl, OSRM_MAX_TABLE_COORDINATES overrides MaxTableCoordinates.
public sealed class OsrmOptions
{
    public const string Section = "Osrm";

    public string? BaseUrl { get; set; }
    public string Profile { get; set; } = "driving";
    public int TimeoutSeconds { get; set; } = 20;
    public int MaxTableCoordinates { get; set; } = 100;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}
