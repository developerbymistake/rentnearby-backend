using System.Text.Json;

namespace RentNearBy.Core.DTOs.Buddy;

public class ExpenseLinkRequestDto
{
    public string Kind { get; set; } = string.Empty;
    public Guid Id { get; set; }
}

public class ExpenseShareDto
{
    public Guid UserId { get; set; }
    public int Share { get; set; }
}

// E2 PUT /trips/{tripId}/expenses/{expenseId}. baseVersion 0 = create.
public class SaveExpenseRequestDto
{
    public Guid OpId { get; set; }
    public int BaseVersion { get; set; }
    public int Amount { get; set; }
    public string? Title { get; set; }
    public string? Category { get; set; }
    public int DayNumber { get; set; }
    public ExpenseLinkRequestDto? Link { get; set; }
    public Guid PaidByUserId { get; set; }
    public string? SplitMode { get; set; }
    public List<Guid>? Sharers { get; set; }
    public List<ExpenseShareDto>? Shares { get; set; }
    public string? Note { get; set; }
    public bool PaymentsAcknowledged { get; set; }
}

// E5
public class SkipSuggestionRequestDto
{
    public string Kind { get; set; } = string.Empty;
    public Guid SourceId { get; set; }
}

public class ShareWithItemDto
{
    public Guid ExpenseId { get; set; }
    public int BaseVersion { get; set; }
}

// E7
public class ShareWithRequestDto
{
    public Guid OpId { get; set; }
    public Guid UserId { get; set; }
    public List<ShareWithItemDto>? Items { get; set; }
    public bool PaymentsAcknowledged { get; set; }
}

public class ExpenseLinkDto
{
    public string Kind { get; set; } = string.Empty;
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool Cancelled { get; set; }
}

public class ExpenseDetailDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Amount { get; set; }
    public int DayNumber { get; set; }
    public DateOnly? Date { get; set; }
    public ExpenseLinkDto? Link { get; set; }
    public Guid PaidByUserId { get; set; }
    public string SplitMode { get; set; } = string.Empty;
    public Guid? LeftoverUserId { get; set; }
    public string? Note { get; set; }
    public int Version { get; set; }
    public int EditedTimes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool Deleted { get; set; }
    public List<ExpenseShareDto> Shares { get; set; } = [];
}

public class SaveExpenseResponseDto
{
    public ExpenseDetailDto Expense { get; set; } = new();
    public long BalancesVersion { get; set; }
    public bool Created { get; set; }
}

public class BalancesVersionDto
{
    public long BalancesVersion { get; set; }
}

public class ShareWithResponseDto
{
    public List<ExpenseDetailDto> Expenses { get; set; } = [];
    public long BalancesVersion { get; set; }
    public List<Guid> Notified { get; set; } = [];
}

public class ExpenseConflictDto
{
    public ExpenseDetailDto? Current { get; set; }
}

public class ExpenseConflictsDto
{
    public List<Guid> Conflicts { get; set; } = [];
}

// E1
public class ExpenseTotalsDto
{
    public long Spent { get; set; }
    public int ExpenseCount { get; set; }
    public long MyShare { get; set; }
    public long MyPaid { get; set; }
}

public class ExpenseDayDto
{
    public int Day { get; set; }
    public DateOnly? Date { get; set; }
    public long Total { get; set; }
    public int Count { get; set; }
}

public class ExpenseSuggestionDto
{
    public string Kind { get; set; } = string.Empty;
    public Guid SourceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Amount { get; set; }
    public int Day { get; set; }
}

public class ExpenseLinkOptionDto
{
    public string Kind { get; set; } = string.Empty;
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public int Day { get; set; }
    public bool Cancelled { get; set; }
}

public class LatePromptItemDto
{
    public Guid ExpenseId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int DayNumber { get; set; }
    public int Amount { get; set; }
    public Guid PaidByUserId { get; set; }
    public int Version { get; set; }
    public List<ExpenseShareDto> Shares { get; set; } = [];
}

public class LatePromptDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime JoinedAt { get; set; }
    public int Count { get; set; }
    public long Total { get; set; }
    public List<LatePromptItemDto> Items { get; set; } = [];
}

public class ExpenseListItemDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Amount { get; set; }
    public int DayNumber { get; set; }
    public Guid PaidByUserId { get; set; }
    public int SharedBy { get; set; }
    public int MyShare { get; set; }
    public int Version { get; set; }
    public bool Edited { get; set; }
    public string? LinkKind { get; set; }
    public Guid? LinkId { get; set; }
    public bool BookingCancelled { get; set; }
    public DateTime CreatedAt { get; set; }
}

// Header fields are null on a cursor page (only items and nextCursor are sent).
public class ExpenseListDto
{
    public Guid? TripId { get; set; }
    public string? TripTitle { get; set; }
    public string? OwnerName { get; set; }
    public string? Role { get; set; }
    public bool? IsOwner { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? DayCount { get; set; }
    public int? DayToday { get; set; }
    public bool? TripEnded { get; set; }
    public bool? Frozen { get; set; }
    public DateOnly? ClosesOn { get; set; }
    public long? BalancesVersion { get; set; }
    public bool? LimitReached { get; set; }
    public ExpenseTotalsDto? Totals { get; set; }
    public List<MoneyPersonDto>? Travellers { get; set; }
    public List<ExpenseDayDto>? Days { get; set; }
    public List<ExpenseSuggestionDto>? Suggestions { get; set; }
    public List<ExpenseLinkOptionDto>? Links { get; set; }
    public LatePromptDto? LatePrompt { get; set; }
    public List<ExpenseListItemDto> Items { get; set; } = [];
    public string? NextCursor { get; set; }
}

// E4
public class ExpenseHistoryLineDto
{
    public int Version { get; set; }
    public DateTime At { get; set; }
    public Guid ActorUserId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public List<JsonElement> Changes { get; set; } = [];
    public int AmountAfter { get; set; }
    public int SharedBy { get; set; }
}

public class ExpenseDetailResponseDto
{
    public ExpenseDetailDto Expense { get; set; } = new();
    public List<MoneyPersonDto> People { get; set; } = [];
    public List<ExpenseHistoryLineDto> History { get; set; } = [];
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
}

// One element of E8 payments; reused by BE4 responses. State: Open | SaidPaid | Received.
public class PaymentRowDto
{
    public Guid? PaymentId { get; set; }
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }
    public int Amount { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTime? SaidPaidAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public Guid? ReceivedByUserId { get; set; }
    public DateTime? AfterNotReceivedAt { get; set; }
    public bool AfterEdit { get; set; }
    public bool CanSayPaid { get; set; }
    public bool CanWithdraw { get; set; }
    public bool CanMarkReceived { get; set; }
    public bool CanMarkNotReceived { get; set; }
    public bool CanRemind { get; set; }
    public bool ReminderSentToday { get; set; }
}

public class BalancesReopenedDto
{
    public DateTime At { get; set; }
    public Guid? ExpenseId { get; set; }
    public string? ExpenseTitle { get; set; }
}

public class BalancesMeDto
{
    public long Paid { get; set; }
    public long Share { get; set; }
    public long Net { get; set; }
    public long Received { get; set; }
    public long StillOpen { get; set; }
    public long SaidPaidWaiting { get; set; }
}

public class BalancePersonDto
{
    public MoneyPersonDto Person { get; set; } = new();
    public long Paid { get; set; }
    public long Share { get; set; }
    public long Net { get; set; }
}

// E8
public class BalancesDto
{
    public Guid TripId { get; set; }
    public string TripTitle { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsOwner { get; set; }
    public bool TripEnded { get; set; }
    public bool Frozen { get; set; }
    public DateOnly? EndDate { get; set; }
    public long BalancesVersion { get; set; }
    public bool Settled { get; set; }
    public int PaymentsDone { get; set; }
    public int PaymentsTotal { get; set; }
    public BalancesReopenedDto? Reopened { get; set; }
    public BalancesMeDto Me { get; set; } = new();
    public List<BalancePersonDto> People { get; set; } = [];
    public List<PaymentRowDto> Payments { get; set; } = [];
}

// E17
public class SummaryPersonDto
{
    public MoneyPersonDto Person { get; set; } = new();
    public long Share { get; set; }
    public long Paid { get; set; }
}

public class SummaryCategoryDto
{
    public string Category { get; set; } = string.Empty;
    public long Amount { get; set; }
}

public class SummaryDayDto
{
    public int Day { get; set; }
    public DateOnly? Date { get; set; }
    public long Total { get; set; }
}

public class SummaryLinkDto
{
    public string Kind { get; set; } = string.Empty;
    public Guid Id { get; set; }
    public long Total { get; set; }
}

public class ExpenseSummaryDto
{
    public Guid TripId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int DayCount { get; set; }
    public int TravellerCount { get; set; }
    public long Total { get; set; }
    public int ExpenseCount { get; set; }
    public bool Settled { get; set; }
    public int OpenPayments { get; set; }
    public List<SummaryPersonDto> PerPerson { get; set; } = [];
    public List<SummaryCategoryDto> ByCategory { get; set; } = [];
    public List<SummaryDayDto> ByDay { get; set; } = [];
    public List<SummaryLinkDto> ByLink { get; set; } = [];
}

// Service outcome of the expense lane: a value, or an error type (BuddyErrorTypes) with optional data.
public sealed class ExpenseResult<T>
{
    public T? Value { get; private init; }
    public string? Error { get; private init; }
    public object? ErrorData { get; private init; }
    public bool Created { get; private init; }
    public bool IsOk => Error is null;

    public static ExpenseResult<T> Ok(T value, bool created = false) => new() { Value = value, Created = created };
    public static ExpenseResult<T> Fail(string error, object? data = null) => new() { Error = error, ErrorData = data };
}
