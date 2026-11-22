using RentNearBy.Core.DTOs.Owner;

namespace RentNearBy.Core.Models.Owner;

// Contract 2.3: the rules of a special price (lane BE2). Dates are inclusive nights; the counter rolls at 00:00 IST (4.3).
public static class StaySpecialPriceRules
{
    public const int PercentStep = 5;
    public const int MinPercent = 5;
    public const int LowerMaxPercent = 50;
    public const int HigherMaxPercent = 100;

    public static int Nights(DateOnly from, DateOnly to) => to.DayNumber - from.DayNumber + 1;

    public static bool IsDirection(string? direction) => direction is SpecialPriceDirections.Lower or SpecialPriceDirections.Higher;

    public static bool PercentOk(string direction, int percent)
        => percent % PercentStep == 0 && percent >= MinPercent
           && percent <= (direction == SpecialPriceDirections.Lower ? LowerMaxPercent : HigherMaxPercent);

    // The 400 SpecialPriceInvalid line, or null when the rule is valid. keepFrom = the stored start of an edited rule: an ongoing
    // rule keeps its start even when it is before today.
    public static string? Invalid(string direction, int percent, DateOnly from, DateOnly to, DateOnly today, DateOnly? keepFrom = null)
    {
        if (!PercentOk(direction, percent))
            return direction == SpecialPriceDirections.Lower ? OwnerErrorTypes.SpecialPricePercentLower : OwnerErrorTypes.SpecialPricePercentHigher;
        if (to < from || Nights(from, to) > OwnerLimits.SpecialMaxNights) return OwnerErrorTypes.SpecialPriceTooLong;
        var keepsStart = keepFrom is { } kept && kept == from;
        if (!keepsStart && (from < today || from > today.AddDays(OwnerLimits.SpecialStartWithinDays))) return OwnerErrorTypes.SpecialPriceStartTooLate;
        if (to < today) return OwnerErrorTypes.SpecialPriceStartTooLate;
        return null;
    }

    public static bool Overlaps(DateOnly aFrom, DateOnly aTo, DateOnly bFrom, DateOnly bTo) => aFrom <= bTo && bFrom <= aTo;

    // A rule counts as active (limit, overlap, lists) until its last night has passed.
    public static bool IsActive(DateOnly to, DateOnly today) => to >= today;

    public static bool IsLive(bool isPaused, bool pausedByPlan) => !isPaused && !pausedByPlan;

    // 3.2 SpecialPrices: active rules incl. paused and plan-paused against the plan number (create only; an edit is never limited).
    public static bool LimitReached(int active, int limit) => active >= limit;

    public static int SavesUsed(DateOnly? day, int count, DateOnly today) => day == today ? count : 0;

    public static bool SavesLimitReached(DateOnly? day, int count, DateOnly today) => SavesUsed(day, count, today) >= OwnerLimits.SpecialSavesPerDay;

    public static int SavesLeft(DateOnly? day, int count, DateOnly today) => Math.Max(0, OwnerLimits.SpecialSavesPerDay - SavesUsed(day, count, today));

    // Same request as the stored row: a replay (no write, no counter).
    public static bool SameRule(DateOnly from, DateOnly to, string direction, int percent, DateOnly storedFrom, DateOnly storedTo, string storedDirection, int storedPercent)
        => from == storedFrom && to == storedTo && direction == storedDirection && percent == storedPercent;

    public static string? Label(string? festivalName)
    {
        if (string.IsNullOrWhiteSpace(festivalName)) return null;
        var t = festivalName.Trim();
        return t.Length <= OwnerLimits.FestivalLabelMax ? t : t[..OwnerLimits.FestivalLabelMax];
    }

    // Festivals whose dates touch the rule's nights (delete-on-act of the FestivalReminder notice, 13.8).
    public static IReadOnlyList<Guid> TouchingFestivals(DateOnly from, DateOnly to, IEnumerable<FestivalDto> festivals)
        => festivals.Where(f => Overlaps(from, to, f.DateFrom, f.DateTo)).Select(f => f.Id).Distinct().ToList();

    // Only active, live rules ever reach the price function.
    public static List<PriceRule> ActiveRules(IEnumerable<StayRuleRow>? rows, DateOnly today)
        => rows == null ? [] : rows.Where(r => r.To >= today).Select(r => new PriceRule(r.From, r.To, r.Lower, r.Percent)).ToList();

    public static List<(DateOnly From, DateOnly To)> ActiveBlocks(IEnumerable<StayBlockRow>? rows, DateOnly today)
        => rows == null ? [] : rows.Where(b => b.To >= today).Select(b => (b.From, b.To)).ToList();
}

// One room type as the listed quote sees it (visible rooms only).
public sealed record ListedRoom(string RoomName, int PricePerNight, int MaxGuests, short? Floor);

// A live rule with its label (the festival name copied at save, owner side only).
public sealed record LabelledRule(PriceRule Rule, string? Label);

public sealed record ListedQuote(int Total, IReadOnlyList<ListedBreakdownLineDto> Breakdown);

// T4 and AU-14a: the listed total of the party for the asked nights, written once on the enquiry. The party's quote lines
// (StayPartyFit) when the party fits; else the cheapest room, one unit (the FROM price the traveller saw). Null = no priced room.
public static class StayListedQuote
{
    // FB-06: a still-Asked row that takes adjacent nights gets the listed total and breakdown of all its nights again,
    // in the same save (no offer exists yet, so "never changed after an offer" holds). No priced room = values unchanged.
    public static void ApplyMerged(RentNearBy.Core.Entities.TripEnquiry row, IReadOnlyList<ListedRoom> rooms, int partyGuests,
        DateOnly mergedIn, DateOnly mergedOut, IReadOnlyList<LabelledRule> rules, System.Text.Json.JsonSerializerOptions json)
    {
        var listed = Compute(rooms, partyGuests > 0 ? partyGuests : null, mergedIn, mergedOut.DayNumber - mergedIn.DayNumber, rules);
        if (listed == null) return;
        row.ListedStayTotal = listed.Total;
        row.ListedStayBreakdownJson = System.Text.Json.JsonSerializer.Serialize(listed.Breakdown, json);
    }

    public static ListedQuote? Compute(IReadOnlyList<ListedRoom> rooms, int? guests, DateOnly checkIn, int nights, IReadOnlyList<LabelledRule> rules)
    {
        var priced = rooms.Where(r => r.PricePerNight > 0).ToList();
        if (priced.Count == 0 || nights <= 0) return null;

        var lines = new List<(string Room, int Units, int Base)>();
        if (guests is { } g && g > 0)
        {
            var quote = StayPartyFit.Quote(
                priced.Select(r => new StayFitRoom(r.RoomName, r.MaxGuests, r.PricePerNight, r.Floor)).ToList(), g, null, false, nights);
            if (quote.Fits)
                lines.AddRange(quote.Lines.Where(l => l.PricePerNight is > 0).Select(l => (l.RoomName, l.Units, l.PricePerNight!.Value)));
        }
        if (lines.Count == 0)
        {
            var cheapest = priced.OrderBy(r => r.PricePerNight).First();
            lines.Add((cheapest.RoomName, 1, cheapest.PricePerNight));
        }

        var plain = rules.Select(r => r.Rule).ToList();
        long total = 0;
        var breakdown = new List<ListedBreakdownLineDto>();
        foreach (var (room, units, basePrice) in lines)
        {
            total += (long)units * StaySpecialPriceMath.Total(basePrice, checkIn, nights, plain);
            breakdown.AddRange(Segments(room, units, basePrice, checkIn, nights, rules, plain));
        }
        var capped = (int)Math.Min(total, StaySpecialPriceMath.MaxPrice);
        return capped < OwnerLimits.ListedStayTotalMin ? null : new ListedQuote(capped, breakdown);
    }

    private static IEnumerable<ListedBreakdownLineDto> Segments(
        string room, int units, int basePrice, DateOnly checkIn, int nights, IReadOnlyList<LabelledRule> rules, IReadOnlyList<PriceRule> plain)
    {
        var runStart = 0;
        var runPrice = 0;
        string? runLabel = null;
        for (var i = 0; i < nights; i++)
        {
            var night = checkIn.AddDays(i);
            var price = StaySpecialPriceMath.PriceOn(basePrice, night, plain);
            var label = rules.FirstOrDefault(r => r.Rule.From <= night && night <= r.Rule.To)?.Label;
            if (i > 0 && (price != runPrice || label != runLabel))
            {
                yield return new ListedBreakdownLineDto(room, units, i - runStart, runPrice, runLabel);
                runStart = i;
            }
            runPrice = price;
            runLabel = label;
        }
        yield return new ListedBreakdownLineDto(room, units, nights - runStart, runPrice, runLabel);
    }
}
