namespace RentNearBy.Core.Models;

// Section "Retention" of appsettings. Rules is case-insensitive so env-var overrides match on Linux.
public sealed class RetentionOptions
{
    public const string Section = "Retention";
    public bool Enabled { get; set; } = true;
    public string WindowStartIst { get; set; } = "01:00";
    public string WindowEndIst { get; set; } = "04:30";
    public int BatchSize { get; set; } = 1000;
    public int MinBatchSize { get; set; } = 100;
    public int PauseMs { get; set; } = 300;
    public int FastBatchMs { get; set; } = 300;
    public int BusyBatchMs { get; set; } = 1500;
    public int BusyActive { get; set; } = 8;
    public int HeavyActive { get; set; } = 15;
    public int MaxPercent { get; set; } = 20;
    public int GuardMinRows { get; set; } = 1000;
    public Dictionary<string, RetentionRuleOptions> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Clamps after binding; safe on an empty section.
    public void Normalize()
    {
        BatchSize = Math.Max(1, BatchSize);
        MinBatchSize = Math.Clamp(MinBatchSize, 1, BatchSize);
        PauseMs = Math.Max(0, PauseMs);
        FastBatchMs = Math.Max(0, FastBatchMs);
        BusyBatchMs = Math.Max(FastBatchMs, BusyBatchMs);
        BusyActive = Math.Max(1, BusyActive);
        HeavyActive = Math.Max(BusyActive, HeavyActive);
        MaxPercent = Math.Clamp(MaxPercent, 1, 50);
        GuardMinRows = Math.Max(0, GuardMinRows);
        if (Rules is null || Rules.Comparer != StringComparer.OrdinalIgnoreCase)
            Rules = new Dictionary<string, RetentionRuleOptions>(Rules ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules.Values.Where(r => r is not null))
            if (rule.Days is < 1) rule.Days = 1;
    }

    // Config entry over the code defaults of the rule. A missing entry means enabled with DefaultDays.
    public RetentionRuleSettings Resolve(string ruleName, int defaultDays, bool defaultEnabled)
    {
        Rules.TryGetValue(ruleName, out var o);
        var enabled = Enabled && (o?.Enabled ?? defaultEnabled);
        var days = Math.Max(1, o?.Days ?? defaultDays);
        return new RetentionRuleSettings(enabled, days, o?.AllowLargeBacklog ?? false);
    }
}

public sealed class RetentionRuleOptions
{
    public bool? Enabled { get; set; }
    public int? Days { get; set; }
    public bool AllowLargeBacklog { get; set; }
}

public readonly record struct RetentionRuleSettings(bool Enabled, int Days, bool AllowLargeBacklog);

public static class RetentionGroups
{
    public const string Notifications = "A.Notifications";
    public const string Devices = "A.Devices";
    public const string Chat = "A.Chat";
    public const string TripHousekeeping = "B.Housekeeping";
    public const string TripEnquiries = "B.Enquiries";
    public const string Sessions = "C.Sessions";

    public static readonly IReadOnlyList<string> All = new[] { Notifications, TripHousekeeping, Sessions, Devices, TripEnquiries, Chat };
}

public enum RetentionStatus { Completed, SlotEnd, Busy, GuardStop, Disabled, LockBusy, Error }

// Statement parameters a rule may use inside its SQL (everything else is rejected by the catalog test).
public static class RetentionParams
{
    public static readonly IReadOnlyList<string> Allowed = new[] { "@cutoff", "@cutoffDate", "@today", "@now", "@n" };
}

// Closed (terminal, deletable) enquiry statuses; the partial index ix_tripenquiries_closed_updated uses the same text.
public static class RetentionSql
{
    public const string ClosedEnquiry =
        "(\"Status\" IN ('NotAvailable','Expired','Declined','CancelledByTraveller','CancelledByOwner','Closed') " +
        "OR (\"Status\" = 'Failed' AND \"FailReason\" IN ('DriverDenied','HoldExpired','ListingRemoved')))";
}

// Declarative rule. EligibleSql holds the protection predicate. OrderBy is a column name or a SQL expression.
public sealed record RetentionRuleDefinition(
    string Name, string Group, string Table, string KeyColumn, string EligibleSql, string OrderBy,
    int DefaultDays, bool DefaultEnabled = true, bool GuardExempt = false, string[]? KeyColumns = null);
