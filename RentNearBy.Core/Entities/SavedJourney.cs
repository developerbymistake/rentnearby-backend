namespace RentNearBy.Core.Entities;

// One saved ways-to-go plan of a traveller (1 to 3 legs). Owner-only; soft deleted.
public class SavedJourney
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? TripId { get; set; }

    public string FromName { get; set; } = string.Empty;
    public decimal FromLatitude { get; set; }
    public decimal FromLongitude { get; set; }
    public string ToName { get; set; } = string.Empty;
    public decimal ToLatitude { get; set; }
    public decimal ToLongitude { get; set; }

    public DateOnly TravelDate { get; set; }
    public short People { get; set; }
    public string PlanKey { get; set; } = string.Empty;
    public short Cars { get; set; }
    public string Status { get; set; } = "NotAsked";
    public int? ListedTotal { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastRefreshedAt { get; set; }
    public bool IsDeleted { get; set; }

    public ICollection<SavedJourneyLeg> Legs { get; set; } = new List<SavedJourneyLeg>();
}
