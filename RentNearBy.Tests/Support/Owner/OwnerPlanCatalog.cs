using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Support.Owner;

// The seeded Free and Plus plans of both kinds, built from PlanKeyCatalog exactly like DataSeeder.SeedPlansAsync
// (no trial since 2026-11-27; a test that needs the trial machinery passes trialDays).
public static class OwnerPlanCatalog
{
    public static readonly Guid StayFreeId = new("b1000000-0000-0000-0000-000000000301");
    public static readonly Guid StayPlusId = new("b1000000-0000-0000-0000-000000000302");
    public static readonly Guid TaxiFreeId = new("b1000000-0000-0000-0000-000000000401");
    public static readonly Guid TaxiPlusId = new("b1000000-0000-0000-0000-000000000402");

    public static Dictionary<string, int> Features(string kind, bool plus)
        => PlanKeyCatalog.For(kind).ToDictionary(k => k.Key, k => plus ? k.SeedFor(kind)!.Plus : k.SeedFor(kind)!.Free);

    public static CatalogPlan Plan(string kind, bool plus, bool enabled = true, Dictionary<string, int>? features = null, int trialDays = 0)
    {
        var f = features ?? Features(kind, plus);
        var id = (kind, plus) switch
        {
            (ListingKinds.Stay, false) => StayFreeId,
            (ListingKinds.Stay, true) => StayPlusId,
            (ListingKinds.Taxi, false) => TaxiFreeId,
            _ => TaxiPlusId,
        };
        return new CatalogPlan(id, kind, plus ? "PLUS" : "FREE", plus ? "Plus" : "Free", null, !plus, enabled, plus ? trialDays : 0, plus ? 1 : 0,
            f, PlanFeatureValues.From(f), [], plus ? $"{kind.ToLowerInvariant()}_plus" : null);
    }

    public static PlanCatalogSnapshot Seeded(bool plusEnabled = true, int trialDays = 0) => new(
    [
        Plan(ListingKinds.Stay, false), Plan(ListingKinds.Stay, true, plusEnabled, trialDays: trialDays),
        Plan(ListingKinds.Taxi, false), Plan(ListingKinds.Taxi, true, plusEnabled, trialDays: trialDays),
    ]);
}
