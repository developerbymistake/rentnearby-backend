using RentNearBy.Core.Models;

namespace RentNearBy.Core.Entities;

public class Trip
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    // Atlas city key = Wikidata item number.
    public long CityKey { get; set; }
    public string CityName { get; set; } = string.Empty;
    public double CityLatitude { get; set; }
    public double CityLongitude { get; set; }
    // Main DB district by point-in-polygon; null = outside every active district.
    public Guid? DistrictId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = TripStatuses.Draft;
    public string PlanJson { get; set; } = "{}";
    public int DayCount { get; set; }
    public int SpotCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
