namespace RentNearBy.Core.Models.Owner;

// Stored = kept for reads or a later phase; ListingLimit keeps its FreeModeListingLimit rule.
public enum PlanKeyKind { Limit, Ranking, Stored }

// Free and Plus seed value of a key for one listing kind; null = the kind has no row for the key.
public sealed record PlanKeySeed(int Free, int Plus);

public sealed record PlanKeyInfo(
    string Key,
    PlanKeyKind Kind,
    bool IsSwitch,
    int Min,
    int Max,
    string[] ListingKinds,
    string Label,
    PlanKeySeed? Stay,
    PlanKeySeed? Taxi,
    string? TaxiLabel = null)
{
    public bool AppliesTo(string listingKind) => ListingKinds.Contains(listingKind);

    public string LabelFor(string listingKind) => listingKind == Models.ListingKinds.Taxi && TaxiLabel != null ? TaxiLabel : Label;

    public PlanKeySeed? SeedFor(string listingKind) => listingKind switch
    {
        Models.ListingKinds.Stay => Stay,
        Models.ListingKinds.Taxi => Taxi,
        _ => null,
    };

    public int Clamp(int value) => Math.Clamp(value, Min, Max);
}

// The one list of plan keys (contract 1.4). The seeder, the admin form, the payment OFF rule and the tests read it.
public static class PlanKeyCatalog
{
    private static readonly string[] Both = [ListingKinds.Stay, ListingKinds.Taxi];
    private static readonly string[] StayOnly = [ListingKinds.Stay];
    private static readonly string[] TaxiOnly = [ListingKinds.Taxi];

    public static readonly IReadOnlyList<PlanKeyInfo> All =
    [
        new(PlanFeatureKeys.ListingLimit, PlanKeyKind.Stored, false, 1, 1, Both, "Listings", new(1, 1), new(1, 1)),
        new(PlanFeatureKeys.Priority, PlanKeyKind.Ranking, false, 0, 999, Both, "First in lists", new(0, 1), new(0, 1)),
        new(PlanFeatureKeys.Featured, PlanKeyKind.Ranking, true, 0, 1, Both, "Featured badge", new(0, 1), new(0, 1)),
        new(PlanFeatureKeys.JobsPosts, PlanKeyKind.Limit, false, 0, 999, StayOnly, "Active job posts", new(1, 4), null),
        new(PlanFeatureKeys.Photos, PlanKeyKind.Limit, false, 1, 12, StayOnly, "Stay photos", new(2, 8), null),
        new(PlanFeatureKeys.RoomTypes, PlanKeyKind.Limit, false, 1, 8, StayOnly, "Room types", new(2, 6), null),
        new(PlanFeatureKeys.Routes, PlanKeyKind.Limit, false, 1, 10, TaxiOnly, "Routes that ring", null, new(1, 5)),
        new(PlanFeatureKeys.RingWeight, PlanKeyKind.Ranking, false, 1, 5, TaxiOnly, "Ring turn weight", null, new(1, 2)),
        new(PlanFeatureKeys.CalendarEntries, PlanKeyKind.Limit, false, 0, 999, Both, "Bookings he adds, active", new(4, 50), new(4, 50),
            TaxiLabel: "Trips he adds, active"),
        new(PlanFeatureKeys.BlockedRanges, PlanKeyKind.Limit, false, 0, 999, StayOnly, "Blocked ranges, active", new(1, 10), null),
        new(PlanFeatureKeys.UpcomingDays, PlanKeyKind.Limit, false, 1, 366, Both, "Upcoming list, days", new(15, 183), new(15, 183)),
        new(PlanFeatureKeys.MonthsAhead, PlanKeyKind.Limit, false, 0, 12, Both, "Month view ahead", new(0, 6), new(0, 6)),
        new(PlanFeatureKeys.MonthSummary, PlanKeyKind.Limit, true, 0, 1, Both, "Month money summary", new(0, 1), new(0, 1)),
        new(PlanFeatureKeys.Reminders, PlanKeyKind.Limit, true, 0, 1, Both, "Morning reminder", new(0, 1), new(0, 1)),
        new(PlanFeatureKeys.SpecialPrices, PlanKeyKind.Limit, false, 0, 999, StayOnly, "Special prices, active", new(1, 5), null),
        new(PlanFeatureKeys.DemandCount, PlanKeyKind.Stored, true, 0, 1, StayOnly, "Travellers asked (phase 2)", new(0, 1), null),
        new(PlanFeatureKeys.StayQuotationsPerMonth, PlanKeyKind.Limit, false, 0, 999, StayOnly, "Quotations a month", new(2, 30), null),
    ];

    private static readonly Dictionary<string, PlanKeyInfo> ByKey = All.ToDictionary(k => k.Key);

    public static PlanKeyInfo Of(string key)
        => ByKey.TryGetValue(key, out var info) ? info : throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown plan feature key");

    public static bool TryOf(string key, out PlanKeyInfo info) => ByKey.TryGetValue(key, out info!);

    public static IEnumerable<PlanKeyInfo> For(string listingKind) => All.Where(k => k.AppliesTo(listingKind));
}
