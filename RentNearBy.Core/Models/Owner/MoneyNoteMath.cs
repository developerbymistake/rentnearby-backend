namespace RentNearBy.Core.Models.Owner;

public readonly record struct MoneyNote(int? FinalAmount, int? AdvanceReceived, bool PaidInFull);

// ChipAmount is 0 when the chip is None (shared money_note_vectors.json).
public readonly record struct MoneyNoteView(int Received, int? Balance, string Chip, int ChipAmount);

// Doc 6.2 (private money notes). Pure; the app mirrors it from the shared money note vectors.
public static class MoneyNoteMath
{
    public const string AdvanceAboveFinal = "Advance cannot be more than the final amount.";
    public const string FinalFirst = "Add the final amount first.";
    public const string AmountRange = "Enter an amount between Rs 1 and Rs 99,00,000.";

    // Error keys of the shared vector file and the app.
    public const string AmountRangeKey = "amountRange";
    public const string FinalNeededKey = "finalNeeded";
    public const string AdvanceAboveFinalKey = "advanceAboveFinal";

    public static string? ErrorKey(string? message) => message switch
    {
        AmountRange => AmountRangeKey,
        FinalFirst => FinalNeededKey,
        AdvanceAboveFinal => AdvanceAboveFinalKey,
        _ => null,
    };

    // null = valid. Range first, then the paid switch, then advance against final.
    public static string? Validate(MoneyNote note)
    {
        if (note.FinalAmount is { } f && (f < OwnerLimits.MoneyMin || f > OwnerLimits.MoneyMax)) return AmountRange;
        if (note.AdvanceReceived is { } a && (a < 0 || a > OwnerLimits.MoneyMax)) return AmountRange;
        if (note.PaidInFull && note.FinalAmount is null) return FinalFirst;
        if (note.FinalAmount is { } final && note.AdvanceReceived is { } adv && adv > final) return AdvanceAboveFinal;
        return null;
    }

    public static int Received(MoneyNote note)
        => note.PaidInFull && note.FinalAmount is { } f ? f : note.AdvanceReceived ?? 0;

    // Shown only when a final amount is set; never stored.
    public static int? Balance(MoneyNote note)
        => note.FinalAmount is { } f ? Math.Max(0, f - Received(note)) : null;

    public static MoneyNoteView View(MoneyNote note)
    {
        var received = Received(note);
        var balance = Balance(note);
        if (balance is > 0) return new(received, balance, MoneyChips.ToCollect, balance.Value);
        if (balance == 0) return new(received, balance, MoneyChips.PaidInFull, note.FinalAmount ?? 0);
        if (received > 0) return new(received, null, MoneyChips.Received, received);
        return new(received, null, MoneyChips.None, 0);
    }

    // SQL forms over a CalendarEntries alias, the same rule as Received / Balance (C9, C10).
    public static string SqlReceived(string alias)
        => $"(CASE WHEN {alias}.\"PaidInFull\" AND {alias}.\"FinalAmount\" IS NOT NULL THEN {alias}.\"FinalAmount\" ELSE COALESCE({alias}.\"AdvanceReceived\", 0) END)";

    public static string SqlBalance(string alias)
        => $"(CASE WHEN {alias}.\"FinalAmount\" IS NULL THEN NULL ELSE GREATEST(0, {alias}.\"FinalAmount\" - {SqlReceived(alias)}) END)";
}
