using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be3;

internal static class Be3Kit
{
    public static PlanEntitlementService.ActiveRow Row(Guid planId, string source = UserPlanSources.Prepaid)
        => new(Guid.NewGuid(), Guid.NewGuid(), planId, source, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(10));

    public static Guid PlusId(string kind) => kind == ListingKinds.Stay ? OwnerPlanCatalog.StayPlusId : OwnerPlanCatalog.TaxiPlusId;

    public static PlanLimits Free(string kind) => Compute(kind, true, null);

    public static PlanLimits Plus(string kind) => Compute(kind, true, PlusId(kind));

    public static PlanLimits Off(string kind) => Compute(kind, false, null);

    public static PlanLimits OffWithGrant(string kind) => Compute(kind, false, PlusId(kind), UserPlanSources.AdminGrant);

    public static PlanLimits Compute(string kind, bool paymentOn, Guid? planId, string source = UserPlanSources.Prepaid)
        => PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), paymentOn ? PaymentFeatureStates.On() : PaymentFeatureStates.Off(),
            kind, planId is { } id ? Row(id, source) : null);

    public static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Solution root not found");
    }
}
