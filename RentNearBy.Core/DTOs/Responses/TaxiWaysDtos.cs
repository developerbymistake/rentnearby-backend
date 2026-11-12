using System.Text.Json.Serialization;

namespace RentNearBy.Core.DTOs.Responses;

// GET /taxis/ways. Never carries driver phones; stays carry the owner phone (callers are logged in).
public class TaxiWaysResultDto
{
    public string Version { get; set; } = string.Empty;
    public WayPlaceDto From { get; set; } = new();
    public WayPlaceDto To { get; set; } = new();
    public WayBestDto? BestWay { get; set; }
    public List<WayDriverDto> AllTheWay { get; set; } = new();
    public bool AllTheWayHasMore { get; set; }
    public List<WayPlanDto> Plans { get; set; } = new();
    public bool Truncated { get; set; }
    public List<TaxiBrowseDto> LocalTaxis { get; set; } = new();
    public List<StayNearbyDto> Stays { get; set; } = new();
    public WayPlaceDto? ReachFirstPlace { get; set; }
    public bool RoadLines { get; set; }
    // Atlas cities are not available right now: stays and local taxis are empty, the taxi routes still work.
    public bool CitiesUnavailable { get; set; }
}

public class WayPlaceDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lng { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Resolved { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DistrictId { get; set; }
}

public class WayBestDto
{
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class WayDriverDto
{
    public Guid TaxiId { get; set; }
    public string? DriverName { get; set; }
    public string? VehicleTypeName { get; set; }
    public string? VehicleCategory { get; set; }
    public short? Seats { get; set; }
    public string? PlateDisplay { get; set; }
    public string? CityName { get; set; }
    public bool BasedHere { get; set; }
    public string TripReplyLabel { get; set; } = RentNearBy.Core.Models.TaxiTripReplyLabels.New;
    public short PriorityRank { get; set; }
    public bool IsFeatured { get; set; }
    public Guid RouteId { get; set; }
    public string FromName { get; set; } = string.Empty;
    public string ToName { get; set; } = string.Empty;
    public int ListedFare { get; set; }
    public string TripType { get; set; } = string.Empty;
    public bool Partial { get; set; }
    public bool Reversed { get; set; }
    public double? DistanceKm { get; set; }
    public int? Minutes { get; set; }
    public bool HasRoadLine { get; set; }
    // Partial stretch: fare estimated from the road span (null for a whole-route fare).
    public int? EstimatedFare { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    // Door to door: the place where the traveller meets this driver when it is not the start of the journey.
    public string? MeetAtName { get; set; }

    // Detail and change-driver endpoints only.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phone { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WhatsApp { get; set; }
}

public class WayLegDto
{
    public string LegKey { get; set; } = string.Empty;
    public int Index { get; set; }
    public WayPlaceDto From { get; set; } = new();
    public WayPlaceDto To { get; set; } = new();
    public string? MeetingPlace { get; set; }
    // The whole stretch: the top driver's listed fare x CarsNeeded.
    public int ListedFare { get; set; }
    public int CarsNeeded { get; set; } = 1;
    public string? SeatsNote { get; set; }
    // The distinct drivers that together cover the group on this stretch (seat cover).
    public List<Guid> CoverTaxiIds { get; set; } = new();
    public bool FareEstimated { get; set; }
    public bool Partial { get; set; }
    public double? DistanceKm { get; set; }
    public List<string> Passes { get; set; } = new();
    // The cities on the road in order, without the from and to cities.
    public List<WayPassStopDto> PassStops { get; set; } = new();
    public List<WayDriverDto> TopDrivers { get; set; } = new();
    public int DriverCount { get; set; }
    public bool HasRoadLine { get; set; }

    // Plan detail only.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WayDriverDto>? Drivers { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WayGeometryDto? Geometry { get; set; }
}

public class WayPassStopDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lng { get; set; }
}

// GeoJSON LineString, coordinates [lon, lat].
public class WayGeometryDto
{
    public string Type { get; set; } = "LineString";
    public List<double[]> Coordinates { get; set; } = new();
}

public class WayPlanDto
{
    public string PlanKey { get; set; } = string.Empty;
    public int Cars { get; set; }
    public int Total { get; set; }
    public List<WayLegDto> Legs { get; set; } = new();
}

// Same shape as WayPlanDto; every leg carries Drivers (with phones) and Geometry.
public class WayPlanDetailDto : WayPlanDto
{
}

public class WayDriverPageDto
{
    public List<WayDriverDto> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public int Total { get; set; }
    public string LegKey { get; set; } = string.Empty;
}
