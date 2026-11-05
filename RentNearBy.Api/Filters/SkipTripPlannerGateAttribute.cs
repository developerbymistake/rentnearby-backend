namespace RentNearBy.Api.Filters;

// Route metadata: TripPlannerGateFilter lets it through. Driver lead routes carry it, because journey leads
// (/taxis/ways/ask) are sent while the planner is off and must stay answerable; the execution gate still applies.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SkipTripPlannerGateAttribute : Attribute;
