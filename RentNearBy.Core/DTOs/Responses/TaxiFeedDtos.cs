namespace RentNearBy.Core.DTOs.Responses;

public static class TaxiTierKeys
{
    public const string OnRoute = "onRoute";
    public const string Passing = "passing";
    public const string BasedNearFrom = "basedNearFrom";
    public const string InTo = "inTo";
}

// Taxi list item of the nearby feed. Never carries coordinates; DistanceKm is the grid-snapped pin, rounded up to 5 km steps.
public class TaxiFeedItemDto : TaxiBrowseDto
{
    public string? StateName { get; set; }
    public int? DistanceKm { get; set; }
    public string LabelKey { get; set; } = "far";
    public bool IsNear { get; set; }
    // True only for drivers who run this route (listed on it, or passing along it).
    public bool ListedForRoute { get; set; }

    public static TaxiFeedItemDto From(TaxiBrowseDto s) => new()
    {
        Id = s.Id, DriverName = s.DriverName, VehicleTypeName = s.VehicleTypeName, VehicleCategory = s.VehicleCategory,
        Seats = s.Seats, PlateDisplay = s.PlateDisplay, CityId = s.CityId, CityName = s.CityName, DistrictName = s.DistrictName,
        MinFare = s.MinFare, RouteCount = s.RouteCount, LocalDayRate = s.LocalDayRate, LocalDayKm = s.LocalDayKm,
        TripReplyLabel = s.TripReplyLabel, MatchedFare = s.MatchedFare, MatchedFromName = s.MatchedFromName,
        MatchedToName = s.MatchedToName, MatchedTripType = s.MatchedTripType, MatchReversed = s.MatchReversed,
        PriorityRank = s.PriorityRank, IsFeatured = s.IsFeatured, CreatedAt = s.CreatedAt,
    };
}

public class TaxiTierDto
{
    public string Key { get; set; } = string.Empty;
    public string HeaderKey { get; set; } = string.Empty;
    // Only for basedNearFrom and inTo.
    public NearbyHeaderDto? Header { get; set; }
    public List<TaxiFeedItemDto> Items { get; set; } = [];
    public bool HasMore { get; set; }
    public string? NextToken { get; set; }
    public bool Collapsed { get; set; }
}

public class TaxiRouteFeedDto
{
    public NearbyHeaderDto Header { get; set; } = new();
    public List<TaxiTierDto> Tiers { get; set; } = [];
}
