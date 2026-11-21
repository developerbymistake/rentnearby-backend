using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner.Be4;

// Admin (AdminOnly): A1 plan keys and plan values (o_pl_18), A5 special prices report (o_pl_19).
public static class AdminPlanKeyHandlers
{
    // A1 / R-14: the catalog the admin form is drawn from, so a new key can never miss a field.
    public static IResult GetKeys() => OkResponse(Keys());

    public static IReadOnlyList<AdminPlanKeyDto> Keys() => PlanKeyCatalog.All
        .Select(k => new AdminPlanKeyDto(
            k.Key, k.Kind.ToString(), k.IsSwitch, k.Min, k.Max, k.ListingKinds, k.Label,
            k.ListingKinds.ToDictionary(kind => kind, k.LabelFor)))
        .ToList();

    // A1 (AU-24): every key of the plan's kind with its value; a key that was never stored reads as its fallback.
    public static async Task<IResult> GetPlanValues(Guid id, IPlanCatalogCache catalog)
    {
        var plan = (await catalog.GetAsync()).GetPlan(id);
        if (plan is null) return NotFoundResponse("Plan not found");
        return OkResponse(new AdminPlanValuesDto(plan.Id, plan.FeatureKey, plan.IsDefault, PlanValueRules.WireValues(plan.FeatureKey, plan.Values)));
    }

    // A5 (AU-24): active special prices, 20 a page, keyset (DateFrom, Id); bigOffOnly = "40% or more off".
    public static async Task<IResult> GetSpecialPriceReport(AdminOwnerReads reads, bool bigOffOnly = false, string? cursor = null, CancellationToken ct = default)
    {
        if (!OwnerHttp.TryDateCursor(cursor, out var after)) return OwnerHttp.BadCursor();
        return OkResponse(await reads.SpecialPriceReportAsync(bigOffOnly, after, ct));
    }
}
