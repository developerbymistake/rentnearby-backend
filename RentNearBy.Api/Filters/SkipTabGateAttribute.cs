namespace RentNearBy.Api.Filters;

// Endpoint metadata: TabGateFilter lets the route through even when its group's tab is off (app-wide routes
// that happen to live under a gated group, e.g. /listings/context).
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SkipTabGateAttribute : Attribute;
