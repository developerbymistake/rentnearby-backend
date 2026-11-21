using RentNearBy.Core.Interfaces;

namespace RentNearBy.Core.Models.Owner;

public sealed record PlanValueBelowFree(string Key, int Free, int Plus);

// Admin plan values (section 9 A1): each value inside its key's Min..Max, only keys of the plan's kind, and for every Limit key
// Plus (any paid plan) >= Free (the default plan), checked in both directions of an edit.
public static class PlanValueRules
{
    // null = valid.
    public static string? KeyError(string featureKey, string key, int value)
    {
        if (!PlanKeyCatalog.TryOf(key, out var info)) return $"Unknown plan feature key: {key}";
        if (!info.AppliesTo(featureKey)) return $"{info.Label} is not a {featureKey} plan value";
        if (value < info.Min || value > info.Max) return $"{info.Label} must be between {info.Min} and {info.Max}";
        return null;
    }

    public static PlanValueBelowFree? BelowFree(string featureKey, PlanFeatureValues free, PlanFeatureValues plus)
    {
        foreach (var info in PlanKeyCatalog.For(featureKey).Where(k => k.Kind == PlanKeyKind.Limit))
        {
            var f = free.Get(info.Key);
            var p = plus.Get(info.Key);
            if (p < f) return new PlanValueBelowFree(info.Key, f, p);
        }
        return null;
    }

    // The stored features after an edit: the request's keys win, a key the request leaves out keeps its stored value.
    public static Dictionary<string, int> Merge(IReadOnlyDictionary<string, int> stored, IEnumerable<(string Key, int Value)> requested)
    {
        var merged = new Dictionary<string, int>(stored);
        foreach (var (key, value) in requested) merged[key] = value;
        return merged;
    }

    // Wire values of GET /admin/plans/{id}: every key of the kind, switches as bool, a missing stored key as its fallback.
    public static Dictionary<string, object> WireValues(string featureKey, PlanFeatureValues values)
        => PlanKeyCatalog.For(featureKey).ToDictionary(k => k.Key, k => k.IsSwitch ? (object)(values.Get(k.Key) > 0) : values.Get(k.Key));
}

// AU-17: the payment start date is announced at least 7 days ahead.
public static class PaymentAnnounceRules
{
    public static bool IsFarEnough(DateOnly announceDate, DateOnly todayIst) => announceDate >= todayIst.AddDays(OwnerLimits.AnnounceLeadDays);
}

// A4 (o_pl_19): the plan line of the admin review screen, e.g. "Free · Plus ended" when rows are hidden on a free plan.
public static class AdminPlanStateRules
{
    public static string Line(PlanLimits limits, int hiddenRows)
    {
        if (!limits.PaymentEnabled) return $"{limits.PlanName} · payment off";
        return hiddenRows > 0 && !limits.IsPaid ? $"{limits.PlanName} · Plus ended" : limits.PlanName;
    }
}
