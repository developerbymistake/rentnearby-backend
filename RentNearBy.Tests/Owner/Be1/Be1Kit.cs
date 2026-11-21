using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be1;

// Plan limits and owner contexts of lane BE1 tests, built from the seeded catalog exactly as the server computes them.
internal static class Be1Kit
{
    public static readonly DateOnly Today = new(2026, 11, 21);

    public static PlanLimits Free(string kind) => PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), PaymentFeatureStates.On(), kind, null);

    public static PlanLimits Plus(string kind)
    {
        var plan = kind == ListingKinds.Stay ? OwnerPlanCatalog.StayPlusId : OwnerPlanCatalog.TaxiPlusId;
        var row = new PlanEntitlementService.ActiveRow(Guid.NewGuid(), Guid.NewGuid(), plan, UserPlanSources.Prepaid, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(10));
        return PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), PaymentFeatureStates.On(), kind, row);
    }

    public static PlanLimits Off(string kind) => PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), PaymentFeatureStates.Off(), kind, null);

    public static OwnerContext Context(string kind, PlanLimits limits, DateOnly? today = null, long activeManual = 0, long activeBlocks = 0, long activeSpecials = 0, bool remindersOn = true)
        => new(Guid.NewGuid(), kind, Guid.NewGuid(), "Approved", null, null, remindersOn, null, 0, null, 0, limits, today ?? Today,
            [activeManual, activeBlocks, activeSpecials, null]);

    public static CalendarEntryRow Manual(DateOnly from, DateOnly to, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), Kind = CalendarEntryKinds.Manual, DateFrom = from, DateTo = to, Label = "Phone booking",
    };

    public static CalendarEntryRow Auto(DateOnly from, DateOnly to, bool cancelled = false) => new()
    {
        Id = Guid.NewGuid(), Kind = CalendarEntryKinds.Auto, SourceType = CalendarSourceTypes.StayEnquiry, SourceId = Guid.NewGuid(),
        DateFrom = from, DateTo = to, Label = "Neha", Cancelled = cancelled,
    };
}
