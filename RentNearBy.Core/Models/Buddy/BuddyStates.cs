namespace RentNearBy.Core.Models.Buddy;

internal static class BuddySql
{
    public static string List(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}

public static class BuddyRequestStatuses
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Declined = "Declined";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
    public const string ClosedByBlock = "ClosedByBlock";
    public const string ClosedByDeletion = "ClosedByDeletion";

    public static readonly IReadOnlyList<string> All = [Pending, Accepted, Declined, Cancelled, Expired, ClosedByBlock, ClosedByDeletion];
    public static readonly string SqlList = BuddySql.List(All);
}

public static class BuddyRequestSources
{
    public const string Search = "Search";
    public const string Qr = "Qr";

    public static readonly IReadOnlyList<string> All = [Search, Qr];
    public static readonly string SqlList = BuddySql.List(All);
}

public static class TripMemberStatuses
{
    public const string Invited = "Invited";
    public const string Joined = "Joined";
    public const string Declined = "Declined";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
    public const string Left = "Left";
    public const string Removed = "Removed";

    public static readonly IReadOnlyList<string> All = [Invited, Joined, Declined, Cancelled, Expired, Left, Removed];
    public static readonly IReadOnlyList<string> Former = [Left, Removed];
    public static readonly IReadOnlyList<string> InviteEnded = [Declined, Cancelled, Expired];
    public static readonly string SqlList = BuddySql.List(All);
    public static readonly string InviteEndedSqlList = BuddySql.List(InviteEnded);
}

public static class ExpenseCategories
{
    public const string Stay = "Stay";
    public const string Taxi = "Taxi";
    public const string Food = "Food";
    public const string Fuel = "Fuel";
    public const string Tickets = "Tickets";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = [Stay, Taxi, Food, Fuel, Tickets, Other];
    public static readonly string SqlList = BuddySql.List(All);
}

public static class SplitModes
{
    public const string AllTravellers = "All";
    public const string Chosen = "Chosen";
    public const string Custom = "Custom";

    public static readonly IReadOnlyList<string> All = [AllTravellers, Chosen, Custom];
    public static readonly string SqlList = BuddySql.List(All);
}

public static class ExpenseLinkKinds
{
    public const string Stay = "Stay";
    public const string Taxi = "Taxi";

    public static readonly IReadOnlyList<string> All = [Stay, Taxi];
    public static readonly string SqlList = BuddySql.List(All);
}

public static class RevisionKinds
{
    public const string Added = "Added";
    public const string Edited = "Edited";
    public const string SharedWith = "SharedWith";
    public const string Deleted = "Deleted";

    public static readonly IReadOnlyList<string> All = [Added, Edited, SharedWith, Deleted];
    public static readonly string SqlList = BuddySql.List(All);
}

// Stored and wire spelling; the engine enum LedgerPaymentState.SaysPaid is mapped at the edge (TripLedger).
public static class PaymentStates
{
    public const string Open = "Open";
    public const string SaidPaid = "SaidPaid";
    public const string Received = "Received";
    public const string Withdrawn = "Withdrawn";
    public const string NotReceived = "NotReceived";

    public static readonly IReadOnlyList<string> All = [SaidPaid, Received, Withdrawn, NotReceived];
    public static readonly IReadOnlyList<string> Counting = [SaidPaid, Received];
    public static readonly IReadOnlyList<string> Ended = [Withdrawn, NotReceived];
    public static readonly string SqlList = BuddySql.List(All);
    public static readonly string CountingSqlList = BuddySql.List(Counting);
    public static readonly string EndedSqlList = BuddySql.List(Ended);

    public static LedgerPaymentState ToEngine(string state) => state switch
    {
        SaidPaid => LedgerPaymentState.SaysPaid,
        Received => LedgerPaymentState.Received,
        Withdrawn => LedgerPaymentState.Withdrawn,
        NotReceived => LedgerPaymentState.NotReceived,
        Open => LedgerPaymentState.Open,
        _ => throw new ArgumentException($"Unknown payment state {state}", nameof(state)),
    };

    public static string FromEngine(LedgerPaymentState state) => state switch
    {
        LedgerPaymentState.SaysPaid => SaidPaid,
        LedgerPaymentState.Received => Received,
        LedgerPaymentState.Withdrawn => Withdrawn,
        LedgerPaymentState.NotReceived => NotReceived,
        _ => Open,
    };
}

// Kinds stored in UserNotices.
public static class NoticeKinds
{
    public const string BuddyRequest = "BuddyRequest";
    public const string TripInvite = "TripInvite";
    public const string PaymentSaidPaid = "PaymentSaidPaid";
    public const string SettleReminder = "SettleReminder";
    public const string PaymentNotReceived = "PaymentNotReceived";
    public const string ExpensesChanged = "ExpensesChanged";
    public const string TripRemoved = "TripRemoved";
    public const string TripDeleted = "TripDeleted";
    public const string BuddyAccepted = "BuddyAccepted";
    public const string NewBuddy = "NewBuddy";
    public const string TripJoined = "TripJoined";
    public const string PlanUpdated = "PlanUpdated";
    public const string ExpenseAdded = "ExpenseAdded";
    public const string ShareChanged = "ShareChanged";
    // Owner roles (contract 5.3, R-0). TripId is the entity slot: NoTrip, or the festival id for FestivalReminder.
    public const string StayEditApproved = "StayEditApproved";
    public const string StayEditRejected = "StayEditRejected";
    public const string FestivalReminder = "FestivalReminder";
    public const string PlanLimitsChanged = "PlanLimitsChanged";
    public const string PlansStarting = "PlansStarting";

    public static readonly IReadOnlyList<string> Owner = [StayEditApproved, StayEditRejected, FestivalReminder, PlanLimitsChanged, PlansStarting];

    public static readonly IReadOnlyList<string> All =
    [
        BuddyRequest, TripInvite, PaymentSaidPaid, SettleReminder, PaymentNotReceived, ExpensesChanged, TripRemoved, TripDeleted,
        BuddyAccepted, NewBuddy, TripJoined, PlanUpdated, ExpenseAdded, ShareChanged,
        StayEditApproved, StayEditRejected, FestivalReminder, PlanLimitsChanged, PlansStarting,
    ];
    public static readonly IReadOnlyList<string> WithAmount = [PaymentSaidPaid, SettleReminder, PaymentNotReceived, ExpenseAdded, ShareChanged];
    // Rows of a viewer that go when he is removed from the trip (or leaves, plan rows only).
    public static readonly IReadOnlyList<string> GoneOnRemove = [PlanUpdated, ExpenseAdded, ShareChanged];
    public static readonly string SqlList = BuddySql.List(All);

    public static readonly Guid NoTrip = Guid.Empty;
}

public static class ReportScopes
{
    public const string Listing = "Listing";
    public const string User = "User";

    public static readonly IReadOnlyList<string> All = [Listing, User];
    public static readonly string SqlList = BuddySql.List(All);
}

public static class UserReportContexts
{
    public const string Request = "Request";
    public const string Buddy = "Buddy";
    public const string Find = "Find";
    public const string Trip = "Trip";

    public static readonly IReadOnlyList<string> All = [Request, Buddy, Find, Trip];
}

public static class TripAccessStatuses
{
    public const string Owner = "Owner";
    public const string Joined = TripMemberStatuses.Joined;
    public const string Left = TripMemberStatuses.Left;
    public const string Removed = TripMemberStatuses.Removed;
    public const string Deleted = "Deleted";
}
