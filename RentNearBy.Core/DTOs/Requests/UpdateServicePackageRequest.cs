namespace RentNearBy.Core.DTOs.Requests;

// Partial-patch semantics: null = don't change (matches every other UpdateXRequest in this codebase).
// The pricing trio (Price/OriginalPrice/DiscountPercent) is itself nullable at the entity level (a
// Price of null is a legitimate "Get Custom Quote" value, not "no update"), so the admin form always
// resubmits all three together when editing pricing — the handler applies them as one group rather
// than patching each independently.
//
// EXACT TRIGGER RULE (implemented in ServiceCatalogHandlers.AdminUpdateServicePackage — read this
// before wiring the admin form): the pricing group is considered "touched" iff ANY of Price,
// OriginalPrice, DiscountPercent, IsStartingAtPrice is non-null. When touched, ALL FOUR fields are
// written verbatim from this request (including nulling out ones left null) — none are patched
// independently. This is the only way to null out an existing Price (switch to "Get Custom Quote"):
// the admin form MUST send IsStartingAtPrice=false (never leave it null) whenever it nulls
// Price/OriginalPrice/DiscountPercent for that transition, otherwise all four fields would read as
// "untouched" and nothing would change. If none of the four are set, pricing is left completely
// alone — safe for a patch that only touches e.g. Name or SortOrder.
public record UpdateServicePackageRequest(
    string? Name,
    int? Price, int? OriginalPrice, int? DiscountPercent, bool? IsStartingAtPrice,
    int? DurationDays, int? DurationNights, string? PriceUnit,
    int? SortOrder, bool? IsFeatured, bool? IsActive);
