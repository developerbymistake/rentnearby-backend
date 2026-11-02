using RentNearBy.Core.Models;

namespace RentNearBy.Core.Entities;

public class Trip
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid CityId { get; set; }
    public Guid DistrictId { get; set; }
    public long? CityKey { get; set; }
    public string CityName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = TripStatuses.Draft;
    public string PlanJson { get; set; } = "{}";
    public int DayCount { get; set; }
    public int SpotCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
