namespace RentNearBy.Core.Models.Owner;

// Codes of the Home attention rows (doc 6.9, AU-19, R-3). The server sends codes, priorities and facts; the app owns the words.
public static class OwnerAttentionCodes
{
    public const string EnquiriesWaiting = "EnquiriesWaiting";
    public const string ChangeOrCounter = "ChangeOrCounter";
    public const string OfferAccepted = "OfferAccepted";
    public const string PaymentIssue = "PaymentIssue";
    public const string NotificationsOff = "NotificationsOff";
    public const string PlanHidden = "PlanHidden";
    public const string Reports = "Reports";
    public const string EditRejected = "EditRejected";
    public const string Festival = "Festival";
    public const string ShareListing = "ShareListing";
    public const string AddShareDetails = "AddShareDetails";
    public const string TravellerChoseYou = "TravellerChoseYou";
    public const string NoPushToken = "NoPushToken";
    public const string MissedRings = "MissedRings";
    public const string GetReady = "GetReady";
    public const string AddLocalRate = "AddLocalRate";
    public const string AddRoutes = "AddRoutes";

    // Row numbers of doc 6.9 with the R-3 swap (ShareListing 10 above AddShareDetails 11, as frame o_st_9 draws).
    public static readonly IReadOnlyDictionary<string, int> StayPriority = new Dictionary<string, int>
    {
        [EnquiriesWaiting] = 1, [ChangeOrCounter] = 2, [OfferAccepted] = 3, [PaymentIssue] = 4, [NotificationsOff] = 5,
        [PlanHidden] = 6, [Reports] = 7, [EditRejected] = 8, [Festival] = 9, [ShareListing] = 10, [AddShareDetails] = 11,
    };

    public static readonly IReadOnlyDictionary<string, int> TaxiPriority = new Dictionary<string, int>
    {
        [TravellerChoseYou] = 1, [NoPushToken] = 2, [NotificationsOff] = 3, [MissedRings] = 4, [PaymentIssue] = 5,
        [PlanHidden] = 6, [Reports] = 7, [GetReady] = 8, [AddLocalRate] = 9, [Festival] = 10, [AddRoutes] = 11,
    };
}

public static class PlanHiddenReasons
{
    public const string PlusEnded = "PlusEnded";
    public const string PlansStarted = "PlansStarted";
}

public static class ChangeOrCounterKinds
{
    public const string Change = "Change";
    public const string Counter = "Counter";
}

public sealed record OwnerAttentionRow(string Code, int Priority, IReadOnlyDictionary<string, object?> Args);

public sealed record OwnerHiddenFacts(int Photos, int RoomTypes, int Routes, int SpecialPrices)
{
    public static readonly OwnerHiddenFacts None = new(0, 0, 0, 0);

    public int Total => Photos + RoomTypes + Routes + SpecialPrices;
}

public sealed record OwnerFestivalFacts(Guid Id, string Name, DateOnly DateFrom, DateOnly DateTo, string? StateName, int DaysAway);

public sealed record StayAttentionFacts(
    int NewCount,
    DateTime? SoonestReplyByUtc,
    bool AnyUrgent,
    Guid? ChangeEnquiryId,
    string? ChangeKind,
    string? ChangeTravellerName,
    Guid? AcceptedEnquiryId,
    string? AcceptedTravellerName,
    DateOnly? AcceptedCheckIn,
    DateOnly? AcceptedCheckOut,
    bool PaymentIssue,
    OwnerHiddenFacts Hidden,
    bool PlansStartedNotice,
    int ReportCount,
    string? FirstReportReason,
    string? EditRejectedReason,
    IReadOnlyList<string> EditRejectedFields,
    bool EditRejectedInWindow,
    OwnerFestivalFacts? Festival,
    bool IsApproved,
    DateTime? ApprovedAtUtc,
    bool HasShareDetails,
    int EnquiriesEverReceived,
    string? Slug);

public sealed record TaxiAttentionFacts(
    Guid? SelectedOfferId,
    DateTime? ConfirmByUtc,
    bool HasValidToken,
    int RingMissStreak,
    bool PaymentIssue,
    OwnerHiddenFacts Hidden,
    bool PlansStartedNotice,
    int ReportCount,
    string? FirstReportReason,
    bool RingReady,
    bool HasLocalRate,
    OwnerFestivalFacts? Festival,
    int VisibleRoutes,
    int RoutesLimit,
    string? BaseCityName,
    string? SuggestedToCityName,
    bool IsApproved);

// Every true row in priority order (doc 6.9). The app shows 2 and "See all (N)"; NotificationsOff is added by the app only.
public static class OwnerAttentionRules
{
    public const int AddShareDetailsDays = 7;

    public static IReadOnlyList<OwnerAttentionRow> Stay(StayAttentionFacts f, DateTime nowUtc)
    {
        var rows = new List<OwnerAttentionRow>();
        void Add(string code, params (string Key, object? Value)[] args)
            => rows.Add(new(code, OwnerAttentionCodes.StayPriority[code], args.ToDictionary(a => a.Key, a => a.Value)));

        if (f.NewCount > 0)
            Add(OwnerAttentionCodes.EnquiriesWaiting, ("count", f.NewCount), ("replyBy", f.SoonestReplyByUtc), ("urgent", f.AnyUrgent));
        if (f.ChangeEnquiryId is { } changeId)
            Add(OwnerAttentionCodes.ChangeOrCounter, ("enquiryId", changeId), ("kind", f.ChangeKind ?? ChangeOrCounterKinds.Change),
                ("travellerName", f.ChangeTravellerName));
        if (f.AcceptedEnquiryId is { } acceptedId)
            Add(OwnerAttentionCodes.OfferAccepted, ("enquiryId", acceptedId), ("travellerName", f.AcceptedTravellerName),
                ("checkIn", f.AcceptedCheckIn), ("checkOut", f.AcceptedCheckOut));
        if (f.PaymentIssue) Add(OwnerAttentionCodes.PaymentIssue);
        if (f.Hidden.Photos + f.Hidden.RoomTypes > 0)
            Add(OwnerAttentionCodes.PlanHidden, ("photos", f.Hidden.Photos), ("roomTypes", f.Hidden.RoomTypes), ("routes", 0),
                ("reason", f.PlansStartedNotice ? PlanHiddenReasons.PlansStarted : PlanHiddenReasons.PlusEnded));
        if (f.ReportCount > 0) Add(OwnerAttentionCodes.Reports, ("count", f.ReportCount), ("firstReason", f.FirstReportReason));
        if (f.EditRejectedInWindow && f.EditRejectedReason != null)
            Add(OwnerAttentionCodes.EditRejected, ("reason", f.EditRejectedReason), ("fields", f.EditRejectedFields));
        if (f.Festival is { } fest) Add(OwnerAttentionCodes.Festival, FestivalArgs(fest));
        if (f.IsApproved && f.EnquiriesEverReceived == 0 && f.Slug != null) Add(OwnerAttentionCodes.ShareListing, ("slug", f.Slug));
        if (f.IsApproved && !f.HasShareDetails && f.ApprovedAtUtc is { } approved && nowUtc - approved <= TimeSpan.FromDays(AddShareDetailsDays))
            Add(OwnerAttentionCodes.AddShareDetails);
        return rows.OrderBy(r => r.Priority).ToList();
    }

    public static IReadOnlyList<OwnerAttentionRow> Taxi(TaxiAttentionFacts f)
    {
        var rows = new List<OwnerAttentionRow>();
        void Add(string code, params (string Key, object? Value)[] args)
            => rows.Add(new(code, OwnerAttentionCodes.TaxiPriority[code], args.ToDictionary(a => a.Key, a => a.Value)));

        if (f.SelectedOfferId is { } offerId) Add(OwnerAttentionCodes.TravellerChoseYou, ("offerId", offerId), ("confirmBy", f.ConfirmByUtc));
        if (f.IsApproved && !f.HasValidToken) Add(OwnerAttentionCodes.NoPushToken);
        if (f.RingMissStreak >= TaxiRequests.TaxiDispatchRules.RingMissLimit) Add(OwnerAttentionCodes.MissedRings, ("count", f.RingMissStreak));
        if (f.PaymentIssue) Add(OwnerAttentionCodes.PaymentIssue);
        if (f.Hidden.Routes > 0)
            Add(OwnerAttentionCodes.PlanHidden, ("photos", 0), ("roomTypes", 0), ("routes", f.Hidden.Routes),
                ("reason", f.PlansStartedNotice ? PlanHiddenReasons.PlansStarted : PlanHiddenReasons.PlusEnded));
        if (f.ReportCount > 0) Add(OwnerAttentionCodes.Reports, ("count", f.ReportCount), ("firstReason", f.FirstReportReason));
        if (f.IsApproved && !f.RingReady && f.HasValidToken && f.RingMissStreak < TaxiRequests.TaxiDispatchRules.RingMissLimit)
            Add(OwnerAttentionCodes.GetReady);
        if (f.IsApproved && !f.HasLocalRate) Add(OwnerAttentionCodes.AddLocalRate);
        if (f.Festival is { } fest) Add(OwnerAttentionCodes.Festival, FestivalArgs(fest));
        if (f.IsApproved && f.VisibleRoutes < f.RoutesLimit)
            Add(OwnerAttentionCodes.AddRoutes, ("fromCityName", f.BaseCityName), ("toCityName", f.SuggestedToCityName));
        return rows.OrderBy(r => r.Priority).ToList();
    }

    private static (string, object?)[] FestivalArgs(OwnerFestivalFacts f) =>
    [
        ("festivalId", f.Id), ("name", f.Name), ("dateFrom", f.DateFrom), ("dateTo", f.DateTo), ("daysAway", f.DaysAway),
    ];
}

// R-2: the one status line under the Home title. Paused > PlansStarted > NewNeedReply > CheckInsThisWeek > Featured > none.
public static class OwnerStatusLineCodes
{
    public const string Paused = "Paused";
    public const string PlansStarted = "PlansStarted";
    public const string NewNeedReply = "NewNeedReply";
    public const string CheckInsThisWeek = "CheckInsThisWeek";
    public const string Featured = "Featured";
}

public sealed record OwnerStatusLine(string Code, IReadOnlyDictionary<string, object?> Args);

public static class OwnerStatusLineRules
{
    public static OwnerStatusLine? Choose(bool isApproved, bool switchOn, bool plansStartedToday, int newCount, int checkInsThisWeek,
        bool isPaid, bool isFeatured, string? cityName)
    {
        if (isApproved && !switchOn) return new(OwnerStatusLineCodes.Paused, Empty);
        if (plansStartedToday) return new(OwnerStatusLineCodes.PlansStarted, Empty);
        if (newCount > 0) return new(OwnerStatusLineCodes.NewNeedReply, new Dictionary<string, object?> { ["count"] = newCount });
        if (checkInsThisWeek > 0) return new(OwnerStatusLineCodes.CheckInsThisWeek, new Dictionary<string, object?> { ["count"] = checkInsThisWeek });
        if (isApproved && isPaid && isFeatured) return new(OwnerStatusLineCodes.Featured, new Dictionary<string, object?> { ["city"] = cityName });
        return null;
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();
}

// Doc 6.10 / AU-8: "this week" = the 7 full IST days before today; a number under 10 (or unknown) is sent as null.
public static class OwnerWeeklyRules
{
    public const int Days = 7;

    public static DateOnly From(DateOnly todayIst) => todayIst.AddDays(-Days);

    public static DateOnly To(DateOnly todayIst) => todayIst.AddDays(-1);

    public static int? Shown(long? value) => value is { } v && v >= OwnerLimits.WeeklyMinShown ? (int)Math.Min(v, int.MaxValue) : null;
}

// Plan card state (doc 8.4b). Payment OFF wins; then the active row; then a Play issue without a row; else Free.
public static class OwnerPlanCardStates
{
    public const string Free = "Free", Trial = "Trial", Plus = "Plus", Cancelled = "Cancelled", Grace = "Grace",
        OnHold = "OnHold", Ended = "Ended", AdminGrant = "AdminGrant", PaymentOff = "PaymentOff";

    public static string Of(bool paymentEnabled, string? activeSource, string? playState, bool playAutoRenew, bool endedRecently)
    {
        if (!paymentEnabled) return PaymentOff;
        if (activeSource != null)
        {
            if (activeSource == UserPlanSources.AdminGrant) return AdminGrant;
            if (playState == PlayStates.InGracePeriod) return Grace;
            if (activeSource == UserPlanSources.Trial) return Trial;
            if (playState == PlayStates.Canceled || (playState != null && !playAutoRenew)) return Cancelled;
            return Plus;
        }
        if (playState == PlayStates.OnHold) return OnHold;
        return endedRecently ? Ended : Free;
    }
}
