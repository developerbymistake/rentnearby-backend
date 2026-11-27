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
    // True once the traveller sent a valid name; the server never retitles such a trip.
    public bool NameChosen { get; set; }
    // Invite GUID, returned only to the owner by GET /trips/{id}/invite. Database default gen_random_uuid().
    public Guid InviteCode { get; set; }
    public string Status { get; set; } = TripStatuses.Draft;
    public string PlanJson { get; set; } = "{}";
    public int DayCount { get; set; }
    public int SpotCount { get; set; }
    // App-generated id of the first create; a replay of the same create returns this row.
    public Guid? ClientTripId { get; set; }
    // Copied from the plan on every save; EndDate = StartDate + DayCount - 1.
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTime? CompletedAt { get; set; }
    // Set once when this trip raised the owner's CompletedTripsCount (TripCompletionCounter).
    public DateTime? CompletionCountedAt { get; set; }
    public DateTime? MutedAt { get; set; }
    // Bakhli social: a public post IS the trip (contract 1.5). Post fields are written by PublicTripService only.
    public bool IsPublic { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? PostText { get; set; }
    public short? PostDays { get; set; }
    public short? PostWindowMonths { get; set; }
    public int? PostStartMonth { get; set; }
    public string JoinGender { get; set; } = "A";
    public short? Seats { get; set; }
    public string[] PostCityNames { get; set; } = [];
    public string? PostStateName { get; set; }
    // PostTravelModes value, null = not set.
    public string? PostTravelMode { get; set; }
    // Seeker plan states (PLAN_STATES_SOCIAL 1), written by SeekerPlanReconcile only. InactiveByPlan: kept read-only after Plus
    // ends (beyond the Free active-trips limit). PausedByPlan: the post was taken off by the plan and comes back with Plus.
    public bool InactiveByPlan { get; set; }
    public bool PausedByPlan { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
