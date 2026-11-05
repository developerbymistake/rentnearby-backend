using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

public static class TripEnquiryKinds
{
    public const string Stay = "Stay";
    public const string Taxi = "Taxi";

    public static readonly string[] All = [Stay, Taxi];
}

public static class TripEnquiryStatuses
{
    public const string Asked = "Asked";
    public const string Offered = "Offered";
    public const string Countered = "Countered";
    public const string ConfirmedByTraveller = "ConfirmedByTraveller";
    public const string ConfirmedByBoth = "ConfirmedByBoth";
    public const string FareSent = "FareSent";
    public const string BookedByTraveller = "BookedByTraveller";
    public const string NotAvailable = "NotAvailable";
    public const string Expired = "Expired";
    public const string Declined = "Declined";
    public const string CancelledByTraveller = "CancelledByTraveller";
    public const string CancelledByOwner = "CancelledByOwner";
    public const string Failed = "Failed";
    public const string Closed = "Closed";

    public static readonly string[] Open = [Asked, Offered, Countered, ConfirmedByTraveller, ConfirmedByBoth, FareSent, BookedByTraveller];
    // Withdrawable and expirable.
    public static readonly string[] Unconfirmed = [Asked, Offered, Countered];
    // Cancellable; a confirmed row holds its nights.
    public static readonly string[] Confirmed = [ConfirmedByTraveller, ConfirmedByBoth];
    public static readonly string[] All =
    [
        Asked, Offered, Countered, ConfirmedByTraveller, ConfirmedByBoth, FareSent, BookedByTraveller,
        NotAvailable, Expired, Declined, CancelledByTraveller, CancelledByOwner, Failed, Closed,
    ];

    // Traveller Withdraw; a block closes these.
    public static readonly string[] Withdrawable = [Asked, Offered, Countered, FareSent];
    // Holds a night or a taxi day; cancel; closure -> Cancelled*.
    public static readonly string[] Booked = [ConfirmedByTraveller, ConfirmedByBoth, BookedByTraveller];

    public static bool IsOpen(string s) => Array.IndexOf(Open, s) >= 0;
    public static bool IsTerminal(string s) => !IsOpen(s);
    public static bool IsUnconfirmed(string s) => Array.IndexOf(Unconfirmed, s) >= 0;
    public static bool IsConfirmed(string s) => Array.IndexOf(Confirmed, s) >= 0;
    public static bool IsWithdrawable(string s) => Array.IndexOf(Withdrawable, s) >= 0;
    public static bool IsBooked(string s) => Array.IndexOf(Booked, s) >= 0;

    // SQL list for partial indexes and raw updates.
    public const string OpenSqlList = "('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller')";
    public const string AllSqlList = "('Asked','Offered','Countered','ConfirmedByTraveller','ConfirmedByBoth','FareSent','BookedByTraveller','NotAvailable','Expired','Declined','CancelledByTraveller','CancelledByOwner','Failed','Closed')";
}

public static class TripFailReasons
{
    public const string HostDenied = "HostDenied";
    public const string DriverDenied = "DriverDenied";
    public const string HoldExpired = "HoldExpired";
    public const string ProblemDriverNoShow = "ProblemDriverNoShow";
    public const string ProblemHostNoRoom = "ProblemHostNoRoom";
    public const string ProblemRoadClosed = "ProblemRoadClosed";
    public const string ProblemOther = "ProblemOther";
    public const string ListingRemoved = "ListingRemoved";
}

// Stored in FailReason on Closed rows.
public static class TripCloseReasons
{
    public const string Withdrawn = "Withdrawn";
    public const string BookedElsewhere = "BookedElsewhere";
    // The traveller moved the trip dates and this unconfirmed row fell outside the new window.
    public const string TripDatesChanged = "TripDatesChanged";
    public const string TripDeleted = "TripDeleted";
    public const string AccountDeleted = "AccountDeleted";
    public const string AdminClosed = "AdminClosed";
}

public static class TripAdvanceStatuses
{
    public const string None = "None";
    public const string Pending = "Pending";
    public const string ProofSent = "ProofSent";
    public const string Received = "Received";
    public const string HoldExpired = "HoldExpired";

    public static readonly string[] All = [None, Pending, ProofSent, Received, HoldExpired];
}

public static class TripOfferKinds
{
    public const string Offer = "Offer";
    public const string Counter = "Counter";
    public const string Revised = "Revised";
    public const string ChangeRequest = "ChangeRequest";
}

public static class TripOfferStatuses
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Declined = "Declined";
    public const string Expired = "Expired";
    public const string Superseded = "Superseded";
}

public static class TripHostConfirmAnswers
{
    public const string Yes = "Yes";
    public const string NotMe = "NotMe";
}

public static class TripHostConfirmVia
{
    public const string Link = "Link";
    public const string App = "App";
}

public static class TripSkipReasons
{
    public const string AlreadyOpen = "AlreadyOpen";
    public const string NotAvailable = "NotAvailable";
    public const string Blocked = "Blocked";
    public const string LimitReached = "LimitReached";
    // A booked lead with this driver already exists for other days.
    public const string AlreadyBooked = "AlreadyBooked";
}

public static class TripDisplayStatuses
{
    public const string Asked = "asked";
    public const string Offered = "offered";
    public const string ConfirmedByYou = "confirmedByYou";
    public const string ConfirmedByBoth = "confirmedByBoth";
    public const string NotAvailable = "notAvailable";
    public const string Expired = "expired";
    public const string Declined = "declined";
    public const string Cancelled = "cancelled";
    public const string NotMe = "notMe";
    public const string Closed = "closed";
    public const string FareSent = "fareSent";
    public const string BookedByYou = "bookedByYou";
    public const string HoldExpired = "holdExpired";
}

// Driver Trip leads chips: new = Asked, replied = FareSent, booked = BookedByTraveller/ConfirmedByBoth, closed = the rest.
public static class TripDriverBuckets
{
    public const string New = "new";
    public const string Replied = "replied";
    public const string Booked = "booked";
    public const string Closed = "closed";

    public static readonly string[] All = [New, Replied, Booked, Closed];
    public static readonly string[] NewStatuses = [TripEnquiryStatuses.Asked];
    public static readonly string[] RepliedStatuses = [TripEnquiryStatuses.FareSent];
    public static readonly string[] BookedStatuses = [TripEnquiryStatuses.BookedByTraveller, TripEnquiryStatuses.ConfirmedByBoth];

    public static string Of(string status) => status switch
    {
        TripEnquiryStatuses.Asked => New,
        TripEnquiryStatuses.FareSent => Replied,
        TripEnquiryStatuses.BookedByTraveller or TripEnquiryStatuses.ConfirmedByBoth => Booked,
        _ => Closed,
    };
}

public sealed record TripDriverCounts(int New, int Replied, int Booked, int Closed);

public static class TripAdvanceRules
{
    public const int HoldMinHours = 1, HoldDefaultHours = 6, HoldMaxHours = 48, HoldCheckInCutoffHourIst = 12;
    public const int ReminderMinutes = 60, UpiMax = 60, UpiMin = 5;
    // Shape only; the id is never resolved or checked against a bank.
    public const string UpiPattern = @"^[A-Za-z0-9._-]{2,50}@[A-Za-z]{2,64}$";
    private static readonly Regex Upi = new(UpiPattern, RegexOptions.Compiled);

    public static bool IsValidUpi(string? s)
    {
        if (s == null) return false;
        var v = s.Trim();
        return v.Length >= UpiMin && v.Length <= UpiMax && Upi.IsMatch(v);
    }

    // Latest allowed hold end: min(now + 48 h, check-in day 12:00 IST).
    public static DateTime HoldCap(DateOnly checkIn, DateTime nowUtc)
    {
        var noon = TripExecutionRules.IstMidnightUtc(checkIn).AddHours(HoldCheckInCutoffHourIst);
        var max = nowUtc.AddHours(HoldMaxHours);
        return noon < max ? noon : max;
    }

    public static bool HoldAllowed(DateTime holdUntil, DateOnly checkIn, DateTime nowUtc)
        => holdUntil >= nowUtc.AddHours(HoldMinHours) && holdUntil <= HoldCap(checkIn, nowUtc);

    public static DateTime? NextHostReminder(TripEnquiry e, DateTime nowUtc)
    {
        if (e.AdvanceStatus != TripAdvanceStatuses.ProofSent || e.Status != TripEnquiryStatuses.ConfirmedByBoth) return null;
        if (e.HoldUntil is not { } hold || hold <= nowUtc) return null;
        var last = e.LastHostReminderAt ?? e.ProofSentAt ?? nowUtc;
        var next = last.AddMinutes(ReminderMinutes);
        return next < hold ? next : hold;
    }

    public static string ProofKey(Guid enquiryId, DateTime proofSentAt, int hourIndex)
        => $"proof:{enquiryId}:{proofSentAt.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}:{hourIndex}";

    public static string NotYetKey(Guid enquiryId, DateTime nowUtc)
        => $"notyet:{enquiryId}:{nowUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture)}";

    // side "t" (traveller) | "o" (owner): one push per recipient, one unique key each.
    public static string HoldKey(Guid enquiryId, string side) => $"hold:{enquiryId}:{side}";
}

public static class TripLeadRules
{
    public const int MaxFareEdits = 3, LeadsPerTripPerDay = 10, MaxOpenLeadsPerDay = 3, PhoneVisibleAfterCloseDays = 7, AnsweredWithinHours = 24;
    public const int MaxLeadDays = TripLimits.MaxDays, PickupPlaceMax = 80;
    // "I booked" saves and edits: own hourly key, never the ask quota.
    public const int BookingsPerHour = 20, MaxReplaceIds = TripLimits.MaxDays;
    public const string PickupTimePattern = @"^([01]\d|2[0-3]):[0-5]\d$";

    public static int ListedTotal(int? listedRate, int days) => (listedRate ?? 0) * Math.Max(1, days);
    public static int EditsLeft(short editCount) => Math.Max(0, MaxFareEdits - editCount);

    // Asked / FareSent rows take more days in place; a booked row is never widened.
    public static bool CanMergeDays(string status) => status is TripEnquiryStatuses.Asked or TripEnquiryStatuses.FareSent;

    // Sorted union; null when every requested day is already on the row.
    public static List<int>? MergeDays(IReadOnlyCollection<int>? existing, IReadOnlyCollection<int> requested)
    {
        var have = existing ?? Array.Empty<int>();
        if (requested.All(d => have.Contains(d))) return null;
        return have.Concat(requested).Distinct().OrderBy(d => d).ToList();
    }

    // A fare was given for the old day set; once days are added the driver quotes again.
    public static void ResetForMerge(TripEnquiry row)
    {
        row.Status = TripEnquiryStatuses.Asked;
        row.DriverFare = null;
        row.DriverNote = null;
        row.DriverRepliedAt = null;
        row.DriverFareEditCount = 0;
    }

    // Days a row keeps when another driver takes some of them; empty = the whole row is taken.
    public static List<int> RemainingDays(IReadOnlyCollection<int>? rowDays, IReadOnlyCollection<int> taken)
        => (rowDays ?? Array.Empty<int>()).Where(d => !taken.Contains(d)).Distinct().OrderBy(d => d).ToList();

    // A multi-day fare split by days kept.
    public static int? ProrateFare(int? fare, int keptDays, int allDays)
        => fare is { } f && allDays > 0 ? TripExecutionRules.Saturate((long)f * keptDays / allDays) : fare;

    // Booked past days of the stored row, as day numbers of the current trip start; an edit keeps them.
    public static List<int> KeptPastDays(IReadOnlyList<int>? storedDays, DateOnly? rowCheckIn, DateOnly start, int dayCount, DateOnly todayIst)
    {
        if (storedDays is not { Count: > 0 } || rowCheckIn is not { } first) return [];
        var firstStored = storedDays.Min();
        return storedDays
            .Select(d => first.AddDays(d - firstStored))
            .Where(date => date < todayIst)
            .Select(date => date.DayNumber - start.DayNumber + 1)
            .Where(d => d >= 1 && d <= dayCount)
            .Distinct()
            .OrderBy(d => d)
            .ToList();
    }
}

// Every day of a trip has a night: check-out is the morning after the last day (end = last DAY).
public static class TripDateRules
{
    public static DateOnly CheckOutLimit(DateOnly end) => end.AddDays(1);

    public static bool InsideTrip(DateOnly start, DateOnly end, DateOnly checkIn, DateOnly checkOut)
        => checkIn >= start && checkOut <= CheckOutLimit(end);

    // A row with any night or day outside [start, end + 1), or any row once the trip has no dates.
    public static bool OutsideWindow(DateOnly? start, DateOnly? end, DateOnly? checkIn, DateOnly? checkOut)
    {
        if (start is not { } s || end is not { } e) return true;
        if (checkIn is not { } a || checkOut is not { } b) return false;
        return !InsideTrip(s, e, a, b);
    }

    // Stay check-out from night `day`: explicit nights (capped at the last night) win over allNights.
    public static DateOnly StayCheckOut(DateOnly start, int dayCount, int day, bool allNights, int? nights)
    {
        var checkIn = TripExecutionRules.NightDate(start, day);
        var left = Math.Max(1, dayCount - day + 1);
        if (nights is { } n) return checkIn.AddDays(Math.Clamp(n, 1, left));
        return checkIn.AddDays(allNights ? left : 1);
    }

    // Only a new start date moves the nights; a day-count change is confirmed in the app first.
    public static bool StartMoved(DateOnly? oldStart, DateOnly? newStart) => oldStart != null && oldStart != newStart;

    public const string OutsideTripMessage = "Offer dates must be inside the trip (check-out the morning after the last day).";
}

// Owner list chips: new = Asked/Countered, offered = Offered, confirmed = both confirmed states, closed = the rest.
public static class TripOwnerBuckets
{
    public const string New = "new";
    public const string Offered = "offered";
    public const string Confirmed = "confirmed";
    public const string Closed = "closed";

    public static readonly string[] All = [New, Offered, Confirmed, Closed];
    public static readonly string[] NewStatuses = [TripEnquiryStatuses.Asked, TripEnquiryStatuses.Countered];
    public static readonly string[] OfferedStatuses = [TripEnquiryStatuses.Offered];
    public static readonly string[] ConfirmedStatuses = TripEnquiryStatuses.Confirmed;

    public static string Of(string status) => status switch
    {
        TripEnquiryStatuses.Asked or TripEnquiryStatuses.Countered => New,
        TripEnquiryStatuses.Offered => Offered,
        TripEnquiryStatuses.ConfirmedByTraveller or TripEnquiryStatuses.ConfirmedByBoth => Confirmed,
        _ => Closed,
    };

    // A confirmed booking whose check-out has passed has ended: it lists under closed.
    public static bool HasEnded(DateOnly? checkOut, DateOnly todayIst) => checkOut is { } c && c < todayIst;

    public static string Of(string status, bool ended)
    {
        var bucket = Of(status);
        return bucket == Confirmed && ended ? Closed : bucket;
    }

    public static string Of(string status, DateOnly? checkOut, DateOnly todayIst) => Of(status, HasEnded(checkOut, todayIst));
}

public static class TripEnquiryErrorTypes
{
    public const string TripEnquiryNotFound = "TripEnquiryNotFound";
    public const string EnquiryLimitReached = "EnquiryLimitReached";
    public const string EnquiryAlreadyOpen = "EnquiryAlreadyOpen";
    public const string ListingNotAvailable = "ListingNotAvailable";
    public const string OfferExpired = "OfferExpired";
    public const string OfferNotPending = "OfferNotPending";
    public const string OfferLimit = "OfferLimit";
    public const string NotTripOwner = "NotTripOwner";
    public const string NotEnquiryTraveller = "NotEnquiryTraveller";
    public const string NotEnquiryOwner = "NotEnquiryOwner";
    public const string TripEnquiryClosed = "TripEnquiryClosed";
    public const string TripDatesRequired = "TripDatesRequired";
    public const string DayInPast = "DayInPast";
    public const string NightAlreadyConfirmed = "NightAlreadyConfirmed";
    public const string OfferOutsideTrip = "OfferOutsideTrip";
    public const string PhoneBookingPaused = "PhoneBookingPaused";
    public const string HostLinkResendTooSoon = "HostLinkResendTooSoon";
    public const string HostLinkLimit = "HostLinkLimit";
    public const string TripExecutionDisabled = "TripExecutionDisabled";
    public const string KindNotAvailable = "KindNotAvailable";
    public const string AdvanceNotAllowed = "AdvanceNotAllowed";
    public const string HoldExpired = "HoldExpired";
    public const string TravellerMovedOn = "TravellerMovedOn";
    public const string FareEditLimit = "FareEditLimit";
    public const string DayAlreadyBooked = "DayAlreadyBooked";
    public const string LeadLimitReached = "LeadLimitReached";
    public const string CounterLimit = "CounterLimit";
    public const string CounterNotAllowed = "CounterNotAllowed";
    public const string RevisedNotAllowed = "RevisedNotAllowed";
    public const string ChangeNotAllowed = "ChangeNotAllowed";
    public const string ChangeNotPending = "ChangeNotPending";
    public const string NothingChanged = "NothingChanged";
    public const string TripNotRunning = "TripNotRunning";
    public const string TripNotEnded = "TripNotEnded";
    public const string ProblemOutsideTrip = "ProblemOutsideTrip";
    public const string AskAgainNothing = "AskAgainNothing";
    public const string TripNeedsUpdate = "TripNeedsUpdate";
    public const string TravelDateChanged = "TravelDateChanged";
    public const string TripCompleted = "TripCompleted";
    public const string EnquiryEnded = "EnquiryEnded";
    public const string NightFailed = "NightFailed";
    public const string ConcurrentUpdate = "CONCURRENT_UPDATE";
    public const string CONCURRENT_UPDATE = ConcurrentUpdate;
}

public static class TripOfferRules
{
    public const int MaxCounters = 3, MaxRevised = 5, CounterMinPerNight = 100, ReasonMax = 300;
    // One cap with the stay listing's per-night price.
    public const int OfferMaxPerNight = StayLimits.MaxPrice, CounterMaxPerNight = StayLimits.MaxPrice;

    public static int CounterTotal(int pricePerNight, int units, int nights)
        => TripExecutionRules.Saturate((long)Math.Max(1, nights) * Math.Max(1, units) * pricePerNight);

    // The host's lines (rooms, units, guests) at the guest's per-room price; lines of different prices keep their ratio.
    // Without an offer: one line of `units` rooms.
    public static List<TripOfferLine> CounterLines(IReadOnlyList<TripOfferLine>? offerLines, int perRoomPrice, int units, int guests)
    {
        var lines = (offerLines ?? []).Where(l => l.Units > 0 && l.PricePerNight > 0).ToList();
        if (lines.Count == 0)
            return [new TripOfferLine { Units = Math.Max(1, units), Guests = Math.Max(1, guests), PricePerNight = perRoomPrice }];

        long totalUnits = lines.Sum(l => (long)l.Units);
        long offered = lines.Sum(l => (long)l.Units * l.PricePerNight);
        return lines.Select(l => new TripOfferLine
        {
            RoomName = l.RoomName,
            Units = l.Units,
            Guests = l.Guests,
            PricePerNight = (int)Math.Max(1, Math.Round((double)l.PricePerNight * perRoomPrice * totalUnits / offered, MidpointRounding.AwayFromZero)),
        }).ToList();
    }

    // Price lock: a change request keeps every accepted line's PricePerNight; units follow the requested rooms
    // (extra rooms go to the first line, fewer rooms are taken from the last lines); guests are spread by units.
    public static List<TripOfferLine> ChangeLines(IReadOnlyList<TripOfferLine> accepted, int rooms, int guests)
    {
        rooms = Math.Max(1, rooms);
        guests = Math.Max(1, guests);
        if (accepted.Count == 0) return [new TripOfferLine { Units = rooms, Guests = guests, PricePerNight = 0 }];

        var units = accepted.Select(l => Math.Max(1, l.Units)).ToList();
        var diff = rooms - units.Sum();
        if (diff > 0) units[0] += diff;
        for (var i = units.Count - 1; diff < 0 && i >= 0; i--)
        {
            var take = Math.Min(units[i], -diff);
            units[i] -= take;
            diff += take;
        }

        var kept = accepted.Select((l, i) => (Line: l, Units: units[i])).Where(x => x.Units > 0).ToList();
        var total = kept.Sum(x => x.Units);
        var shares = kept.Select(x => Math.Max(1, guests * x.Units / total)).ToList();
        var rest = guests - shares.Sum();
        if (rest > 0) shares[0] += rest;
        for (var i = shares.Count - 1; rest < 0 && i >= 0; i--)
        {
            var take = Math.Min(shares[i] - 1, -rest);
            shares[i] -= take;
            rest += take;
        }

        return kept.Select((x, i) => new TripOfferLine
        {
            RoomName = x.Line.RoomName,
            Units = x.Units,
            Guests = shares[i],
            PricePerNight = x.Line.PricePerNight,
        }).ToList();
    }

    // Only Accept (traveller, Revised) and AcceptChange (host, ChangeRequest) may move the snapshot.
    public static bool MovesSnapshot(string offerKind, string actorSide) =>
        (offerKind == TripOfferKinds.Revised && actorSide == "traveller") || (offerKind == TripOfferKinds.ChangeRequest && actorSide == "owner");
}

// ListingReport.ListingType for a reported traveller (ListingId = the enquiry id).
public static class ListingReportTypes
{
    public const string TripEnquiry = "TripEnquiry";
}

// Section "TripExecution"; env TRIP_EXECUTION_ENABLED overrides Enabled.
public sealed class TripExecutionOptions
{
    public const string Section = "TripExecution";
    public bool Enabled { get; set; } = true;
    public string PublicBaseUrl { get; set; } = "https://developerbymistake.tech";
}

public static class TripExecutionRules
{
    public const int MaxShortlistPerNight = 3, MaxOpenPerNight = 3, MaxOffersPerEnquiry = 5, MaxLinesPerOffer = 5;
    public const int EnquiryHours = 48, ReplyBeforeHours = 12, FloorHours = 2;
    public const int OfferHours = 24, OfferUrgentHours = 2, OfferFloorHours = 1, UrgentHours = 72;
    public const int HostLinkDays = 7, HostLinkMaxDays = 14, HostLinkResendHours = 24, HostLinkMaxCount = 2, HostLinkOverrideDays = 7;
    public const int NotMeWindowDays = 30, NotMePauseCount = 3, NotMePauseDays = 30;
    public const int PhoneBookingsPerDay = 10, EnquiriesPerHour = 20, EnquiriesPerDay = 60, OwnerActionsPerHour = 60, ConfirmPagePerMinute = 20;
    public const int MaxTextLen = 80, MaxSpotsInText = 8, NoteMax = 300, CancellationMax = 300, BalanceNoteMax = 120, HostNameMax = 60, RoomNameMax = 40;
    public const int MinAmount = 1, MaxAmount = StayLimits.MaxPrice;
    public const string AsksLeftHeader = "X-Trip-Asks-Left";
    public const string TotalRangeMessage = "Total must be between Rs 1 and Rs 99,00,000";
    public const int PushLogRetentionDays = 90, OrphanRetentionDays = 180;

    public static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);

    public static DateTime IstMidnightUtc(DateOnly date)
        => DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue) - IstOffset, DateTimeKind.Utc);

    public static DateOnly TodayIst(DateTime nowUtc) => DateOnly.FromDateTime(nowUtc + IstOffset);

    public static DateOnly NightDate(DateOnly startDate, int day) => startDate.AddDays(day - 1);

    // Inverse of NightDate: the trip day whose night starts on date (TripEnquiry.Day follows CheckIn).
    public static short DayOf(DateOnly startDate, DateOnly date) => (short)(date.DayNumber - startDate.DayNumber + 1);

    public static bool IsUrgent(DateOnly startDate, DateTime nowUtc)
        => IstMidnightUtc(startDate) - nowUtc < TimeSpan.FromHours(UrgentHours);

    // ref = trip start 00:00 IST before the trip, else check-in 00:00 IST; min(now + 48 h, ref - 12 h), floor now + 2 h.
    public static (DateTime ExpiresAt, bool VeryShortNotice) EnquiryExpiry(DateOnly startDate, DateOnly checkIn, DateTime nowUtc)
    {
        var reference = TodayIst(nowUtc) < startDate ? IstMidnightUtc(startDate) : IstMidnightUtc(checkIn);
        var expires = Min(nowUtc.AddHours(EnquiryHours), reference.AddHours(-ReplyBeforeHours));
        var floor = nowUtc.AddHours(FloorHours);
        return expires < floor ? (floor, true) : (expires, false);
    }

    public static DateTime OfferValidUntil(DateOnly startDate, DateOnly checkIn, DateTime nowUtc)
    {
        var hours = IsUrgent(startDate, nowUtc) ? OfferUrgentHours : OfferHours;
        var valid = Min(nowUtc.AddHours(hours), IstMidnightUtc(checkIn));
        var floor = nowUtc.AddHours(OfferFloorHours);
        return valid < floor ? floor : valid;
    }

    public static DateTime HostLinkExpiry(DateOnly checkIn, DateTime nowUtc)
    {
        var expires = Max(nowUtc.AddDays(HostLinkDays), IstMidnightUtc(checkIn));
        var cap = nowUtc.AddDays(HostLinkMaxDays);
        return expires > cap ? cap : expires;
    }

    public static bool NightsOverlap(DateOnly aIn, DateOnly aOut, DateOnly bIn, DateOnly bOut) => aIn < bOut && bIn < aOut;

    // Saturates at int.MaxValue so the MaxAmount check rejects an overflowing total.
    public static int ComputeTotal(IReadOnlyList<TripOfferLine> lines, int nights)
        => Saturate((long)nights * lines.Sum(l => (long)l.Units * l.PricePerNight));

    public static int Saturate(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    public static int Nights(DateOnly? checkIn, DateOnly? checkOut)
        => checkIn is { } a && checkOut is { } b ? Math.Max(0, b.DayNumber - a.DayNumber) : 0;

    public static string DisplayStatus(string status, string? failReason) => status switch
    {
        TripEnquiryStatuses.Asked or TripEnquiryStatuses.Countered => TripDisplayStatuses.Asked,
        TripEnquiryStatuses.Offered => TripDisplayStatuses.Offered,
        TripEnquiryStatuses.FareSent => TripDisplayStatuses.FareSent,
        TripEnquiryStatuses.ConfirmedByTraveller => TripDisplayStatuses.ConfirmedByYou,
        TripEnquiryStatuses.BookedByTraveller => TripDisplayStatuses.BookedByYou,
        TripEnquiryStatuses.ConfirmedByBoth => TripDisplayStatuses.ConfirmedByBoth,
        TripEnquiryStatuses.NotAvailable => TripDisplayStatuses.NotAvailable,
        TripEnquiryStatuses.Expired => TripDisplayStatuses.Expired,
        TripEnquiryStatuses.Declined => TripDisplayStatuses.Declined,
        TripEnquiryStatuses.Failed when failReason is TripFailReasons.HostDenied or TripFailReasons.DriverDenied => TripDisplayStatuses.NotMe,
        TripEnquiryStatuses.Failed when failReason is TripFailReasons.HoldExpired => TripDisplayStatuses.HoldExpired,
        TripEnquiryStatuses.Failed => TripDisplayStatuses.NotAvailable,
        TripEnquiryStatuses.CancelledByTraveller or TripEnquiryStatuses.CancelledByOwner => TripDisplayStatuses.Cancelled,
        _ => TripDisplayStatuses.Closed,
    };

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

// TripEnquiry.ContextJson, serialised with TripJson.Options. Built from the trip row, never from the request.
public sealed class TripEnquiryContext
{
    public string? TripTitle { get; set; }
    public string City { get; set; } = string.Empty;
    public int? Adults { get; set; }
    public int? Kids { get; set; }
    public int? Rooms { get; set; }
    public string[] Needs { get; set; } = [];
    public string? TravellerName { get; set; }
    public List<TripEnquiryContextDay> Days { get; set; } = new();
    public string? PickupTime { get; set; }
    public string? PickupPlace { get; set; }
    public string Text { get; set; } = string.Empty;
    // Journey leads only: the saved journey and leg the lead belongs to.
    public Guid? JourneyId { get; set; }
    public int? LegIndex { get; set; }
    // Multi-city trips only (null on a one-city trip): the city of the first night / first chosen day, the city count and the trip length.
    public int? CityIndex { get; set; }
    public int? CityCount { get; set; }
    public int? TripDayCount { get; set; }
}

public static class TripEnquiryCity
{
    // City = the leg of that day; index, count and trip length only on a multi-city plan, so one-city JSON is unchanged.
    public static void Apply(TripEnquiryContext context, TripPlan plan, int day, string? rowCityName, int rowDayCount)
    {
        var leg = plan.LegOf(day);
        var name = TripTextSafety.Clean(leg?.CityName);
        context.City = name.Length > 0 ? name : TripTextSafety.Clean(rowCityName);
        var count = plan.Legs?.Count ?? 0;
        if (leg == null || count <= 1) return;
        context.CityIndex = leg.Index;
        context.CityCount = count;
        context.TripDayCount = rowDayCount > 0 ? rowDayCount : plan.Days?.Count ?? 0;
    }
}

public static class TripEnquiryContexts
{
    public static TripEnquiryContext Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TripEnquiryContext();
        try { return JsonSerializer.Deserialize<TripEnquiryContext>(json, TripJson.Options) ?? new TripEnquiryContext(); }
        catch (JsonException) { return new TripEnquiryContext(); }
    }
}

// Journey car between two cities of a trip (Day = travel day). Problem splits of a local taxi also carry Day, so the journey id decides.
public static class TripSegmentRows
{
    public static bool IsSegment(TripEnquiry e)
        => e.Kind == TripEnquiryKinds.Taxi && e.TripId != null && e.Day != null && JourneyIdOf(e) != null;

    public static Guid? JourneyIdOf(TripEnquiry e)
        => string.IsNullOrEmpty(e.ContextJson) || !e.ContextJson.Contains("journeyId", StringComparison.OrdinalIgnoreCase)
            ? null
            : TripEnquiryContexts.Read(e.ContextJson).JourneyId;

    public static int? CarOf(TripEnquiry e) => TripEnquiryContexts.Read(e.ContextJson).LegIndex;

    public static List<TripEnquiry> OfJourney(IEnumerable<TripEnquiry> rows, Guid journeyId)
        => rows.Where(r => IsSegment(r) && JourneyIdOf(r) == journeyId).ToList();

    // A local sightseeing taxi row of a trip (no travel day); journey cars and trip-less journey leads are not.
    public static readonly Expression<Func<TripEnquiry, bool>> LocalTaxi =
        e => e.Kind == TripEnquiryKinds.Taxi && e.TripId != null && e.Day == null;

    public const string RowOwn = "own", RowStale = "stale", RowOther = "other";

    // A driver's open row on the travel day, met by an ask or "I booked" for (journey, car): only this car's row is reused;
    // a row of a journey the plan no longer holds gives way; another car or a local split day keeps it (one open row per day).
    public static string Classify(TripEnquiry existing, TripPlan plan, Guid journeyId, int car)
    {
        if (!IsSegment(existing) || JourneyIdOf(existing) is not { } id) return RowOther;
        if (id == journeyId) return CarOf(existing) == car ? RowOwn : RowOther;
        return TripJourneyLinks.Resolve(plan, id)?.Role == TripStepRoles.Segment ? RowOther : RowStale;
    }
}

public static class TripSegmentContext
{
    public static TripEnquiryContext Build(
        Trip trip, TripPlan plan, TripCityLeg leg, int travelDay, DateOnly date, Guid journeyId,
        int carIndex, int carOf, string fromName, string toName, string? comesFrom, int people, int carsNeeded,
        string? travellerName, string? pickupTime, string? pickupPlace)
    {
        var settings = plan.Settings ?? new TripSettings();
        var from = TripTextSafety.Clean(fromName);
        var name = TripTextSafety.Clean(travellerName);
        var time = !string.IsNullOrWhiteSpace(pickupTime) && Regex.IsMatch(pickupTime.Trim(), TripLeadRules.PickupTimePattern)
            ? pickupTime.Trim()
            : null;
        var context = new TripEnquiryContext
        {
            TripTitle = TripTextSafety.CleanOrNull(trip.Title, 120),
            Adults = settings.Adults ?? people,
            Kids = settings.Kids ?? 0,
            Rooms = null,
            Needs = [],
            TravellerName = name.Length == 0 ? null : name,
            Days = [new TripEnquiryContextDay { Day = travelDay, Date = date, Minutes = 0, Spots = [] }],
            PickupTime = time,
            PickupPlace = TripTextSafety.CleanOrNull(pickupPlace, TripLeadRules.PickupPlaceMax)
                ?? JourneyLeadText.PickupPlace(from, carIndex, carOf),
            JourneyId = journeyId,
            LegIndex = carIndex,
        };
        TripEnquiryCity.Apply(context, plan, travelDay, trip.CityName, trip.DayCount);
        var to = TripTextSafety.Clean(toName);
        if (to.Length == 0) to = TripTextSafety.Clean(leg.CityName);
        if (to.Length == 0) to = context.City;
        var comes = TripTextSafety.CleanOrNull(comesFrom, TripExecutionRules.MaxTextLen);
        context.Text = JourneyLeadText.Build(from, to, date, Math.Max(1, people), carIndex, carOf, comes, Math.Max(1, carsNeeded))
            + TripEnquiryText.CitySuffixOf(context);
        return context;
    }
}

public sealed class TripEnquiryContextDay
{
    public int Day { get; set; }
    public DateOnly? Date { get; set; }
    public int Minutes { get; set; }
    public List<string> Spots { get; set; } = new();
}

// TripEnquiry.PhoneBookingJson.
public sealed class TripPhoneBooking
{
    public string HostName { get; set; } = string.Empty;
    public string HostPhone { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public int Nights { get; set; }
    public int AgreedTotal { get; set; }
    public bool AdvancePaid { get; set; }
    public int? AdvanceAmount { get; set; }
    public string? Note { get; set; }
    public DateTime RecordedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

// TripOffer.LinesJson item.
public sealed class TripOfferLine
{
    public string? RoomName { get; set; }
    public int Units { get; set; }
    public int Guests { get; set; }
    public int PricePerNight { get; set; }
}

// One user as seen by an enquiry DTO; Phone is the raw number, the DTO builder decides who may see it.
public sealed record TripPartyInfo(Guid? Id, string? Name, string? Phone, bool ContactVisible, string? WhatsApp);

public sealed record TripOwnerCounts(int New, int Offered, int Confirmed, int Closed);

// Input of every TripNotifications builder. ChatArgsJson = the full chat-conversation map for the recipient, null without a conversation.
public sealed record TripPushContext(
    Guid RecipientUserId, string TravellerName, string ListingName, string? ChatArgsJson,
    int? Total = null, bool? Confirmed = null, string? OwnerName = null, Guid? JourneyId = null,
    DateOnly? CheckIn = null, DateOnly? CheckOut = null);

public static class TripTextSafety
{
    private static readonly Regex Urls = new(@"(https?://\S+)|(www\.\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static string Clean(string? s) => Clean(s, TripExecutionRules.MaxTextLen);

    public static string Clean(string? s, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        // Line breaks and tabs become a space; other control characters are dropped.
        foreach (var c in s)
        {
            if (!char.IsControl(c)) sb.Append(c);
            else if (char.IsWhiteSpace(c)) sb.Append(' ');
        }
        var text = Urls.Replace(sb.ToString(), " ");
        text = Spaces.Replace(text, " ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd();
    }

    public static string? CleanOrNull(string? s, int maxLength)
    {
        var text = Clean(s, maxLength);
        return text.Length == 0 ? null : text;
    }
}

public static class TripEnquiryText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Build(TripEnquiryContext ctx, DateOnly checkIn, DateOnly checkOut, bool allNights)
    {
        var nights = Math.Max(0, checkOut.DayNumber - checkIn.DayNumber);
        var sb = new StringBuilder();
        sb.Append("Hi, trip to ").Append(ctx.City).Append(", ")
          .Append(checkIn.ToString("dd MMM", Inv)).Append(" to ").Append(checkOut.ToString("dd MMM", Inv));
        if (allNights && nights > 1) sb.Append(" (").Append(nights).Append(" nights)");
        sb.Append(", ").Append(People(ctx.Adults, ctx.Kids));
        if (ctx.Rooms is { } rooms) sb.Append(", ").Append(rooms).Append(rooms == 1 ? " room" : " rooms");
        sb.Append('.');

        var needs = NeedsText(ctx.Needs);
        if (needs != null) sb.Append(" We need ").Append(needs).Append('.');

        foreach (var day in ctx.Days.OrderBy(d => d.Day))
        {
            if (day.Spots.Count == 0) continue;
            sb.Append(" Day ").Append(day.Day).Append(" spots: ").Append(SpotsText(day.Spots)).Append('.');
        }

        sb.Append(" Is a room free? Please share the best price.");
        sb.Append(CitySuffixOf(ctx));
        return sb.ToString();
    }

    // Taxi lead: checkOut = last chosen day + 1.
    public static string BuildLead(TripEnquiryContext ctx, DateOnly checkIn, DateOnly checkOut)
    {
        var last = checkOut.AddDays(-1);
        var sb = new StringBuilder();
        sb.Append("Trip to ").Append(ctx.City).Append(", ").Append(checkIn.ToString("dd MMM", Inv));
        if (last > checkIn) sb.Append(" to ").Append(last.ToString("dd MMM", Inv));
        sb.Append(", ").Append(People(ctx.Adults, ctx.Kids)).Append('.');

        var time = TimeText(ctx.PickupTime);
        var place = string.IsNullOrWhiteSpace(ctx.PickupPlace) ? null : ctx.PickupPlace.Trim();
        if (time != null || place != null)
        {
            sb.Append(" Pickup");
            if (time != null) sb.Append(' ').Append(time);
            if (place != null) sb.Append(" at ").Append(place);
            sb.Append('.');
        }

        foreach (var day in ctx.Days.OrderBy(d => d.Day))
        {
            if (day.Spots.Count == 0) continue;
            sb.Append(" Day ").Append(day.Day);
            var duration = Duration(day.Minutes);
            if (duration != null) sb.Append(" (").Append(duration).Append(')');
            sb.Append(": ").Append(SpotsText(day.Spots)).Append('.');
        }
        sb.Append(CitySuffixOf(ctx));
        return sb.ToString();
    }

    public static string CitySuffix(int dayCount, int cityIndex, int cityCount)
        => cityCount <= 1 ? string.Empty : $" Part of a {dayCount}-day trip, {cityIndex} of {cityCount} cities.";

    // Kept inside Build/BuildLead so every rebuild of a multi-city row (split, shrink, replace) keeps the sentence.
    public static string CitySuffixOf(TripEnquiryContext ctx)
        => ctx.CityCount is { } count && count > 1 && ctx.CityIndex is { } index && ctx.TripDayCount is { } days && days > 0
            ? CitySuffix(days, index, count)
            : string.Empty;

    // "08:30" -> "8:30 am"; null when not a valid HH:mm.
    public static string? TimeText(string? hhmm)
        => TimeOnly.TryParseExact(hhmm, "HH:mm", Inv, DateTimeStyles.None, out var t)
            ? t.ToString("h:mm tt", Inv).ToLowerInvariant()
            : null;

    // 335 -> "5 h 35 min"; null for 0.
    public static string? Duration(int minutes)
    {
        if (minutes <= 0) return null;
        var h = minutes / 60;
        var m = minutes % 60;
        if (h == 0) return $"{m} min";
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }

    public static string People(int? adults, int? kids)
    {
        if (adults is not { } a) return "2 people";
        var text = a == 1 ? "1 adult" : $"{a} adults";
        if (kids is { } k && k > 0) text += k == 1 ? ", 1 kid" : $", {k} kids";
        return text;
    }

    public static string? NeedsText(IReadOnlyList<string>? needs)
    {
        if (needs == null) return null;
        var words = needs.Select(NeedWord).Where(w => w != null).Select(w => w!).Distinct().ToList();
        if (words.Count == 0) return null;
        if (words.Count == 1) return words[0];
        return string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1];
    }

    private static string? NeedWord(string need) => need switch
    {
        TripNeeds.NoStairs => "no stairs",
        TripNeeds.KidsSafe => "a safe room for kids",
        TripNeeds.VegFood => "vegetarian food",
        _ => null,
    };

    private static string SpotsText(IReadOnlyList<string> spots)
    {
        var shown = spots.Take(TripExecutionRules.MaxSpotsInText).ToList();
        var text = string.Join(", ", shown);
        var more = spots.Count - shown.Count;
        return more > 0 ? $"{text} +{more} more" : text;
    }
}

// Message.Type values of trip cards and the system-line kinds/texts (section 2.6).
public static class TripMessageTypes
{
    public const string Enquiry = "trip_enquiry";
    public const string Offer = "trip_offer";
    public const string OfferResponse = "trip_offer_response";
    public const string PhoneBooking = "trip_phone_booking";
    public const string AdvanceRequest = "trip_advance_request";
    public const string AdvanceProofSent = "trip_advance_proof_sent";
    public const string Counter = "trip_counter";
    public const string RevisedOffer = "trip_revised_offer";
    public const string ChangeRequest = "trip_change_request";
    public const string System = "system";
}

public static class TripCardStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Expired = "expired";
    public const string Superseded = "superseded";
    public const string Confirmed = "confirmed";
    public const string Denied = "denied";
    public const string ProofSent = "proofSent";
    public const string Received = "received";
    public const string Replaced = "replaced";
    public const string HoldExpired = "holdExpired";
    // A revised offer the guest declined ("Keep Rs x").
    public const string Kept = "kept";
}

public static class TripSystemLines
{
    public const string Withdrawn = "withdrawn";
    public const string BookedElsewhere = "bookedElsewhere";
    public const string NotAvailable = "notAvailable";
    public const string Expired = "expired";
    public const string OfferExpired = "offerExpired";
    public const string HostConfirmed = "hostConfirmed";
    public const string HostDenied = "hostDenied";
    public const string CancelledByTraveller = "cancelledByTraveller";
    public const string CancelledByOwner = "cancelledByOwner";
    public const string ListingRemoved = "listingRemoved";
    public const string TripDeleted = "tripDeleted";
    public const string ConfirmedBoth = "confirmedBoth";
    public const string AdvanceReceived = "advanceReceived";
    public const string HoldExpired = "holdExpired";
    public const string AdminClosed = "adminClosed";
    public const string RevisedAccepted = "revisedAccepted";
    public const string RevisedKept = "revisedKept";
    public const string ChangeAccepted = "changeAccepted";
    public const string ChangeDifferentOffer = "changeDifferentOffer";
    public const string CounterReplaced = "counterReplaced";
    public const string ProblemHostNoRoom = "problemHostNoRoom";
    public const string TripCompleted = "tripCompleted";
    public const string TripDatesChanged = "tripDatesChanged";

    public static string Text(string kind) => kind switch
    {
        TripDatesChanged => "The guest changed the trip dates. This enquiry is closed.",
        RevisedAccepted => "The guest accepted the new price.",
        RevisedKept => "The guest kept the earlier price.",
        ChangeAccepted => "The host accepted the change.",
        ChangeDifferentOffer => "The host sent a different offer.",
        CounterReplaced => "The guest proposed a new price.",
        ProblemHostNoRoom => "The guest says the host could not give the room.",
        TripCompleted => "The guest marked the trip complete.",
        AdvanceReceived => "Advance received (host says).",
        HoldExpired => "The host did not confirm the advance in time.",
        AdminClosed => "Closed by Bakhli.",
        Withdrawn => "The guest withdrew this enquiry.",
        BookedElsewhere => "The guest booked another stay for these dates.",
        NotAvailable => "This stay is no longer available.",
        Expired => "No reply in time. This enquiry expired.",
        OfferExpired => "Offer expired.",
        ConfirmedBoth => "Confirmed by both sides.",
        HostConfirmed => "Host confirmed the booking.",
        HostDenied => "Host says this booking is not theirs.",
        CancelledByTraveller => "The guest cancelled this booking.",
        CancelledByOwner => "The host cancelled this booking.",
        ListingRemoved => "This stay is no longer on Bakhli.",
        TripDeleted => "The guest cancelled the trip.",
        _ => string.Empty,
    };
}

// Push / link wording: "01-02 Nov", "30 Nov - 02 Dec", "01 Nov".
public static class TripDateText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Day(DateOnly d) => d.ToString("dd MMM", Inv);

    public static string Range(DateOnly checkIn, DateOnly checkOut)
        => checkIn.Month == checkOut.Month && checkIn.Year == checkOut.Year
            ? $"{checkIn.ToString("dd", Inv)}-{checkOut.ToString("dd MMM", Inv)}"
            : $"{Day(checkIn)} - {Day(checkOut)}";

    public static string Range(DateOnly? checkIn, DateOnly? checkOut)
        => checkIn is { } a && checkOut is { } b ? Range(a, b) : checkIn is { } c ? Day(c) : string.Empty;

    // Stay wording; the check-out morning is not a stay day: "Night of 05 Nov", "2 nights from 05 Nov".
    public static string StayNights(DateOnly? checkIn, DateOnly? checkOut)
    {
        if (checkIn is not { } a) return string.Empty;
        var n = TripExecutionRules.Nights(checkIn, checkOut);
        return n <= 1 ? $"Night of {Day(a)}" : $"{n} nights from {Day(a)}";
    }

    // "6:00 pm" (IST).
    public static string TimeIst(DateTime utc)
        => (utc + TripExecutionRules.IstOffset).ToString("h:mm tt", Inv).ToLowerInvariant();

    // "Today, 6:00 pm" | "Sat 01 Nov, 6:00 pm" (IST).
    public static string DayTimeIst(DateTime utc, DateTime nowUtc)
    {
        var day = TripExecutionRules.TodayIst(utc);
        var prefix = day == TripExecutionRules.TodayIst(nowUtc) ? "Today" : day.ToString("ddd dd MMM", Inv);
        return $"{prefix}, {TimeIst(utc)}";
    }

    public static string FirstName(string? name)
    {
        var clean = TripTextSafety.Clean(name);
        if (clean.Length == 0) return "Guest";
        var space = clean.IndexOf(' ');
        return space > 0 ? clean[..space] : clean;
    }
}

public static class HostConfirmTokens
{
    // 32 random bytes, base64url without padding = 43 chars. Only the hash is stored.
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
