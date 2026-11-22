using RentNearBy.Core.DTOs.Buddy;

namespace RentNearBy.Core.Models.Buddy;

public enum TripAccessLevel { Owner, Viewer, Former }

// EndedHow: "Left" | "Removed" (Former only).
public sealed record TripAccessInfo(
    Guid TripId, Guid OwnerUserId, string OwnerName, string Title, string Status,
    DateOnly? StartDate, DateOnly? EndDate, int DayCount, int SpotCount, DateTime? CompletedAt, DateTime UpdatedAt,
    TripAccessLevel Level, Guid? MemberId, DateTime? JoinedAt, string? EndedHow);

// Status: "Owner" | "Joined" | "Left" | "Removed". PhotoUrl = the 160 face thumb (null when none).
public sealed record TripTraveller(Guid UserId, string Name, string? Username, bool IsOwner, string Status, DateTime? JoinedAt, string? PhotoUrl = null);

public sealed record TripMoneyCard(Guid TripId, bool HasExpenses, long Spent, int ExpenseCount, int MyShare, int OpenPayments, bool Settled);

public sealed record TripMoneyStanding(Guid TripId, int MyNet, bool HasShare, int SharedCount, Guid? OwesToUserId);

// State: "Open" | "SaidPaid".
public sealed record OpenPaymentLine(MoneyPersonDto From, MoneyPersonDto To, int Amount, string State);

public sealed record TripLedgerPerson(Guid UserId, int Paid, int Share, int Sent, int Got)
{
    public int ExpenseNet => Paid - Share;
    public int SettleNet => Paid - Share + Sent - Got;
}

public sealed record TripLedgerPaymentRow(Guid Id, Guid FromUserId, Guid ToUserId, int Amount, string State,
    DateTime SaidPaidAt, DateTime? ReceivedAt, Guid? ReceivedByUserId, DateTime? EndedAt);

public sealed record TripLedgerOpenRow(Guid FromUserId, Guid ToUserId, int Amount, DateTime? AfterNotReceivedAt);

public sealed record TripLedgerSnapshot(
    Guid TripId, long Version, int ExpenseCount, long TotalSpent,
    IReadOnlyList<TripLedgerPerson> People,
    IReadOnlyList<TripLedgerPaymentRow> Payments,
    IReadOnlyList<TripLedgerOpenRow> Open,
    bool Settled, int OpenPayments, int SaidPaidCount, int ReceivedCount,
    DateTime? ReopenedAt, Guid? ReopenedExpenseId)
{
    public static TripLedgerSnapshot Empty(Guid tripId) =>
        new(tripId, 0, 0, 0, [], [], [], true, 0, 0, 0, null, null);
}

public sealed record NoticeUpsert(Guid UserId, string Kind, Guid TripId, Guid? ActorUserId, Guid? RefId = null, int? Amount = null);

public sealed record PushSpec(Guid UserId, string PushKind, string Key, string Title, string Body, string Route, string ArgsJson, string Tag, TimeSpan? Delay = null);
