namespace RentNearBy.Core.Entities;

// One row per IST date: the morning reminder run of that day (restart safety, no second run).
public class TripReminderRun
{
    public DateOnly RunDate { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int Sent { get; set; }
}
