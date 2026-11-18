using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Core.Entities.TaxiRequests;

// Durable timer row. DoneAt is set by every move to a final state (ck_dueactions_done).
public class DueAction
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public int RoundNo { get; set; }
    public Guid? RequestId { get; set; }
    public DateTime DueAt { get; set; }
    public string State { get; set; } = DueActionStates.Pending;
    public DateTime? LeaseUntil { get; set; }
    public short Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DoneAt { get; set; }
}
