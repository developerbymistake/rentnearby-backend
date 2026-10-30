namespace RentNearBy.Core.DTOs.Responses;

// /plans/me.play: the Active row's subscription, else the latest Mapped one in hold/paused/grace or ended within 30 days.
public class MyPlanPlayDto
{
    public string State { get; set; } = string.Empty;
    public bool AutoRenewEnabled { get; set; }
    public DateTime? ExpiryAt { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    public string ProductId { get; set; } = string.Empty;
    public string? BasePlanId { get; set; }
    public string? OfferId { get; set; }
    public string? ScheduledPlanName { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public bool IsTest { get; set; }
    public bool Acknowledged { get; set; }
    public DateTime? PausedAutoResumeAt { get; set; }
    public string? CancelReason { get; set; }
    public bool OwnsActivePlan { get; set; }
}

// POST /plans/purchases/{id}/verify-play. Outcome: Granted | Pending | AlreadyProcessed | Rejected.
public class VerifyPlayResultDto : PurchaseStatusDto
{
    public string Outcome { get; set; } = string.Empty;
}

// POST /plans/purchases/restore.
public class RestorePlayResultDto
{
    public List<RestorePlayItemResultDto> Results { get; set; } = new();
    public MyPlanDto Me { get; set; } = new();
}

// Outcome: Granted | Unchanged | Pending | Expired | Rejected | OtherAccount | ProductUnknown | Detached | Error.
// PurchaseToken echoes the caller's own token unchanged; Index is its position in the request.
public class RestorePlayItemResultDto
{
    public int Index { get; set; }
    public string PurchaseToken { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public Guid? PurchaseId { get; set; }
    public string? Status { get; set; }
    public DateTime? EndsAt { get; set; }
}

// GET /admin/users/{id}/plans playSubscriptions[].
public class AdminPlaySubscriptionDto
{
    public Guid Id { get; set; }
    public string TokenTail { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string? BasePlanId { get; set; }
    public string State { get; set; } = string.Empty;
    public string MappingStatus { get; set; } = string.Empty;
    public string? DetachedReason { get; set; }
    public DateTime? ExpiryAt { get; set; }
    public bool AutoRenew { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public bool IsTest { get; set; }
    public DateTime CreatedAt { get; set; }
}

// GET /admin/users/{id}/plans current/history[].play.
public class AdminPlayDetailsDto : MyPlanPlayDto
{
    public string TokenTail { get; set; } = string.Empty;
    public string MappingStatus { get; set; } = string.Empty;
    public string? DetachedReason { get; set; }
    public string? PendingServerAction { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public int? RecurringPriceInr { get; set; }
    public string? PriceChangeState { get; set; }
}
