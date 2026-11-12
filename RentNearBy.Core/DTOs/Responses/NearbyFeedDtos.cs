namespace RentNearBy.Core.DTOs.Responses;

public class NearbyHeaderDto
{
    public string? CentreCityName { get; set; }
    public string? CentreStateName { get; set; }
    public double? CentreCityKm { get; set; }
    public string LivesIn { get; set; } = string.Empty;
    public bool FarAway { get; set; }
    public double? NearestKm { get; set; }
    public bool CitiesUnavailable { get; set; }
}

public class NearbyPageDto<T>
{
    public NearbyHeaderDto Header { get; set; } = new();
    public List<T> Featured { get; set; } = [];
    public List<T> Items { get; set; } = [];
    public bool HasMore { get; set; }
    public string? NextToken { get; set; }
}

public class StayFeedItemDto : StayBrowseDto
{
    public string? StateName { get; set; }
    public double DistanceKm { get; set; }
    public string LabelKey { get; set; } = "far";
    public bool IsNear { get; set; }
}
