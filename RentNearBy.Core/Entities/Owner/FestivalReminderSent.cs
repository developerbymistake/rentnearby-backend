namespace RentNearBy.Core.Entities.Owner;

// Once-only mark of a festival reminder per owner.
public class FestivalReminderSent
{
    public Guid UserId { get; set; }
    public Guid FestivalDateId { get; set; }
    public DateOnly FestivalDateTo { get; set; }
    public DateTime SentAt { get; set; }
}
