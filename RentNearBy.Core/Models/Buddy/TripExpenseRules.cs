using System.Text.Json;
using RentNearBy.Core.DTOs.Buddy;

namespace RentNearBy.Core.Models.Buddy;

// The stored part of one expense that the rules compare (old copy vs new copy).
public sealed record ExpenseFacts(
    int Amount, string Title, string Category, int DayNumber, string? LinkKind, Guid? LinkId,
    Guid PaidByUserId, string SplitMode, Guid? LeftoverUserId, string? Note, IReadOnlyList<SplitShare> Shares);

public sealed record ExpenseChange(string Field, object? Old, object? New);

public sealed record CleanExpenseInput(
    int Amount, string Title, string Category, int DayNumber, string? LinkKind, Guid? LinkId,
    Guid PaidByUserId, string SplitMode, IReadOnlyList<Guid> Sharers, IReadOnlyList<SplitShare> Shares, string? Note);

public sealed record SplitOutcome(IReadOnlyList<SplitShare> Shares, Guid? LeftoverUserId, string? Error)
{
    public static SplitOutcome Fail(string error) => new([], null, error);
}

// Pure rules of the expense lane (contract 2.3): trip facts, validation, split, eligibility, change diff, who is told.
public static class TripExpenseRules
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static int DayToday(DateOnly? startDate, int dayCount, DateOnly todayIst)
    {
        if (startDate is null) return 1;
        var day = todayIst.DayNumber - startDate.Value.DayNumber + 1;
        return Math.Clamp(day, 1, Math.Max(1, dayCount));
    }

    public static bool TripEnded(DateOnly? endDate, DateOnly todayIst) => endDate is not null && todayIst > endDate.Value;

    public static DateOnly? ClosesOn(DateOnly? endDate, DateTime? completedAt)
    {
        if (endDate is not null) return endDate.Value.AddDays(BuddyLimits.FrozenAfterTripEndDays);
        return completedAt is null ? null : IstTime.TodayIst(completedAt.Value).AddDays(BuddyLimits.FrozenAfterTripEndDays);
    }

    // Closed for changes: today IST is after EndDate + 30 days; without an end date, 30 days after CompletedAt.
    public static bool Frozen(DateOnly? endDate, DateTime? completedAt, DateOnly todayIst)
    {
        var closes = ClosesOn(endDate, completedAt);
        return closes is not null && todayIst > closes.Value;
    }

    public static DateOnly? DateOf(DateOnly? startDate, int dayNumber) => startDate?.AddDays(dayNumber - 1);

    public static int MaxDay(int dayCount) => Math.Max(1, dayCount);

    // Shape checks of E2 (validator and service share them). null = fine.
    public static string? CheckInput(SaveExpenseRequestDto r, out CleanExpenseInput? clean)
    {
        clean = null;
        if (r.Amount < BuddyLimits.MinAmount || r.Amount > BuddyLimits.MaxAmount) return BuddyErrorTypes.InvalidAmount;
        var rawTitle = r.Title?.Trim() ?? string.Empty;
        var title = TripTextSafety.Clean(rawTitle, int.MaxValue);
        if (title.Length == 0 || title.Length > BuddyLimits.TitleMax) return BuddyErrorTypes.TitleRequired;
        if (r.Category is null || !ExpenseCategories.All.Contains(r.Category)) return BuddyErrorTypes.InvalidCategory;
        if (r.DayNumber < 1 || r.DayNumber > short.MaxValue) return BuddyErrorTypes.InvalidDay;
        var note = TripTextSafety.Clean(r.Note, int.MaxValue);
        if (note.Length > BuddyLimits.NoteMax) return BuddyErrorTypes.NoteTooLong;
        if (r.SplitMode is null || !SplitModes.All.Contains(r.SplitMode)) return BuddyErrorTypes.BadRequest;
        if (r.Link is not null && (!ExpenseLinkKinds.All.Contains(r.Link.Kind) || r.Link.Id == Guid.Empty)) return BuddyErrorTypes.LinkNotInTrip;
        if (r.PaidByUserId == Guid.Empty) return BuddyErrorTypes.PayerNotTraveller;

        var sharers = (r.Sharers ?? []).Distinct().ToList();
        var shares = (r.Shares ?? []).Select(s => new SplitShare(s.UserId, s.Share)).ToList();
        if (r.SplitMode == SplitModes.Custom)
        {
            if (shares.Any(s => s.Share < 0)) return BuddyErrorTypes.SharesDoNotAddUp;
            if (shares.Select(s => s.UserId).Distinct().Count() != shares.Count) return BuddyErrorTypes.SharesDoNotAddUp;
            var positive = shares.Count(s => s.Share > 0);
            if (positive == 0) return BuddyErrorTypes.NoSharers;
            if (positive > BuddyLimits.MaxSharers) return BuddyErrorTypes.TooManySharers;
        }
        else
        {
            if (r.SplitMode == SplitModes.Chosen && sharers.Count == 0) return BuddyErrorTypes.NoSharers;
            if (sharers.Count > BuddyLimits.MaxSharers) return BuddyErrorTypes.TooManySharers;
        }

        clean = new CleanExpenseInput(
            r.Amount, title, r.Category, r.DayNumber, r.Link?.Kind, r.Link?.Id, r.PaidByUserId, r.SplitMode,
            sharers, shares, note.Length == 0 ? null : note);
        return null;
    }

    // Current travellers = owner + Joined. On an edit the stored payer and stored sharers stay allowed (former or deleted).
    public static HashSet<Guid> CurrentIds(IReadOnlyList<TripTraveller> travellers)
        => travellers.Where(t => t.IsOwner || t.Status == TripMemberStatuses.Joined).Select(t => t.UserId).ToHashSet();

    public static string? CheckPeople(CleanExpenseInput input, IReadOnlySet<Guid> current, ExpenseFacts? stored)
    {
        var payerOk = current.Contains(input.PaidByUserId) || stored?.PaidByUserId == input.PaidByUserId;
        if (!payerOk) return BuddyErrorTypes.PayerNotTraveller;
        var storedSharers = stored?.Shares.Select(s => s.UserId).ToHashSet() ?? [];
        var people = input.SplitMode == SplitModes.Custom
            ? input.Shares.Where(s => s.Share > 0).Select(s => s.UserId)
            : input.Sharers;
        return people.Any(p => !current.Contains(p) && !storedSharers.Contains(p)) ? BuddyErrorTypes.SharerNotTraveller : null;
    }

    // Sharers of "All" with an empty list = every current traveller at save time.
    public static SplitOutcome Split(CleanExpenseInput input, IReadOnlyList<Guid> canonical, IReadOnlySet<Guid> current)
    {
        if (input.SplitMode == SplitModes.Custom)
        {
            var check = TripExpenseEngine.CustomSplit(input.Amount, canonical, input.Shares);
            if (check.Shares.Count > BuddyLimits.MaxSharers) return SplitOutcome.Fail(BuddyErrorTypes.TooManySharers);
            return check.IsValid ? new SplitOutcome(check.Shares, null, null) : SplitOutcome.Fail(BuddyErrorTypes.SharesDoNotAddUp);
        }
        var sharers = input.Sharers.Count > 0 || input.SplitMode != SplitModes.AllTravellers
            ? input.Sharers
            : canonical.Where(current.Contains).ToList();
        if (sharers.Count == 0) return SplitOutcome.Fail(BuddyErrorTypes.NoSharers);
        if (sharers.Count > BuddyLimits.MaxSharers) return SplitOutcome.Fail(BuddyErrorTypes.TooManySharers);
        var split = TripExpenseEngine.EqualSplit(input.Amount, canonical, sharers, input.PaidByUserId);
        return new SplitOutcome(split.Shares, split.LeftoverUserId, null);
    }

    public static ExpenseFacts Facts(CleanExpenseInput input, SplitOutcome split) => new(
        input.Amount, input.Title, input.Category, input.DayNumber, input.LinkKind, input.LinkId,
        input.PaidByUserId, input.SplitMode, split.LeftoverUserId, input.Note, split.Shares);

    public static bool SameShares(IReadOnlyList<SplitShare> a, IReadOnlyList<SplitShare> b)
        => a.Count == b.Count && a.OrderBy(s => s.UserId).SequenceEqual(b.OrderBy(s => s.UserId));

    public static bool MoneyChanged(ExpenseFacts old, ExpenseFacts now)
        => old.Amount != now.Amount || old.PaidByUserId != now.PaidByUserId || !SameShares(old.Shares, now.Shares);

    public static bool AnyChange(ExpenseFacts old, ExpenseFacts now) => Diff(old, now).Count > 0 || old.SplitMode != now.SplitMode;

    // History lines [{field, old, new}] (contract 1.2 TripExpenseRevisions.ChangesJson).
    public static List<ExpenseChange> Diff(ExpenseFacts old, ExpenseFacts now)
    {
        var changes = new List<ExpenseChange>();
        if (old.Amount != now.Amount) changes.Add(new("amount", old.Amount, now.Amount));
        if (old.Title != now.Title) changes.Add(new("title", old.Title, now.Title));
        if (old.Category != now.Category) changes.Add(new("category", old.Category, now.Category));
        if (old.DayNumber != now.DayNumber) changes.Add(new("day", old.DayNumber, now.DayNumber));
        if (old.LinkKind != now.LinkKind || old.LinkId != now.LinkId) changes.Add(new("link", LinkValue(old), LinkValue(now)));
        if (old.PaidByUserId != now.PaidByUserId) changes.Add(new("paidBy", old.PaidByUserId, now.PaidByUserId));
        if (!SameShares(old.Shares, now.Shares)) changes.Add(new("shares", ShareValues(old.Shares), ShareValues(now.Shares)));
        if ((old.Note ?? string.Empty) != (now.Note ?? string.Empty)) changes.Add(new("note", old.Note, now.Note));
        return changes;
    }

    private static object? LinkValue(ExpenseFacts f) => f.LinkKind is null ? null : new { kind = f.LinkKind, id = f.LinkId };

    private static object ShareValues(IReadOnlyList<SplitShare> shares) => shares.Select(s => new { userId = s.UserId, share = s.Share }).ToList();

    // People whose share or paid amount changed (old = null for a create, now = null for a delete). The owner is never told.
    public static List<Guid> Affected(ExpenseFacts? old, ExpenseFacts? now, Guid ownerId)
    {
        var delta = new Dictionary<Guid, (long Paid, long Share)>();
        void Add(ExpenseFacts? f, int sign)
        {
            if (f is null) return;
            delta[f.PaidByUserId] = (delta.GetValueOrDefault(f.PaidByUserId).Paid + sign * f.Amount, delta.GetValueOrDefault(f.PaidByUserId).Share);
            foreach (var s in f.Shares)
                delta[s.UserId] = (delta.GetValueOrDefault(s.UserId).Paid, delta.GetValueOrDefault(s.UserId).Share + sign * s.Share);
        }
        Add(old, -1);
        Add(now, 1);
        return delta.Where(d => d.Key != ownerId && (d.Value.Paid != 0 || d.Value.Share != 0)).Select(d => d.Key).ToList();
    }

    // Edit, delete and share-with after a SaidPaid or Received payment need the owner's confirmation (4.8c, 4.8d).
    public static bool NeedsAcknowledge(bool moneyChanges, int saidPaidCount, int receivedCount, bool acknowledged)
        => moneyChanges && saidPaidCount + receivedCount > 0 && !acknowledged;

    // E7 per item. null = can be shared with the joiner.
    public static string? CheckShareWith(string splitMode, IReadOnlyList<SplitShare> stored, Guid joiner)
    {
        if (splitMode == SplitModes.Custom) return BuddyErrorTypes.NotEqualSplit;
        if (stored.Any(s => s.UserId == joiner)) return BuddyErrorTypes.AlreadySharing;
        if (stored.Count + 1 > BuddyLimits.MaxSharers) return BuddyErrorTypes.TooManySharers;
        return null;
    }

    public static string SharesJson(IReadOnlyList<SplitShare> shares)
        => JsonSerializer.Serialize(shares.Select(s => new ExpenseShareDto { UserId = s.UserId, Share = s.Share }), Json);

    public static List<SplitShare> ParseShares(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        var list = JsonSerializer.Deserialize<List<ExpenseShareDto>>(json, Json) ?? [];
        return list.Select(s => new SplitShare(s.UserId, s.Share)).ToList();
    }

    public static string ChangesJson(IReadOnlyList<ExpenseChange> changes)
        => JsonSerializer.Serialize(changes.Select(c => new { field = c.Field, old = c.Old, @new = c.New }), Json);

    public static List<ExpenseShareDto> ShareDtos(IReadOnlyList<SplitShare> shares, IReadOnlyList<Guid> canonical)
    {
        var order = TripExpenseEngine.Order(canonical, shares.Select(s => s.UserId));
        var byId = shares.ToDictionary(s => s.UserId, s => s.Share);
        return order.Select(id => new ExpenseShareDto { UserId = id, Share = byId[id] }).ToList();
    }

    public static string TaxiLabel(string driverName, string vehicleName)
    {
        var first = driverName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? driverName.Trim();
        return $"Taxi, {first} ({vehicleName.Trim()})";
    }

    // Day of a taxi booking on the plan: its first date against the trip start, clamped; 1 without dates.
    public static int DayOfDate(DateOnly? startDate, DateOnly date, int dayCount)
        => startDate is null ? 1 : Math.Clamp(date.DayNumber - startDate.Value.DayNumber + 1, 1, MaxDay(dayCount));
}
