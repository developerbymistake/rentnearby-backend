namespace RentNearBy.Core.DTOs.Responses;

// Nearby Jobs feed item. CityKey and CityName: nearest live Atlas city to the stay (null = no city, district name only).
public class StayJobFeedItemDto : StayJobSummaryDto
{
    public string? StateName { get; set; }
    public long? CityKey { get; set; }
    public double DistanceKm { get; set; }
    public string LabelKey { get; set; } = "far";
    public bool IsNear { get; set; }
}

// GET /home/nearby-feed: a superset of HomeFeedDto (extra Header, richer stay and taxi items). Null section = off or not asked.
public class HomeNearbyFeedDto
{
    public HomeFeedTabsDto Tabs { get; set; } = new();
    public NearbyHeaderDto? Header { get; set; }
    public List<StayFeedItemDto>? Stays { get; set; }
    public List<TaxiFeedItemDto>? Taxis { get; set; }
    public List<StayBrowseDto>? RecentStays { get; set; }
}
