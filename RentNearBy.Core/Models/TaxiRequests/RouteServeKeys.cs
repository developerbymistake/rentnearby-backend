namespace RentNearBy.Core.Models.TaxiRequests;

// O12: the city keys of one taxi route as they will be saved. Pending = the end's point moved in this save and its city
// lookup did not answer in time, so the null key is not known to be "no city" yet.
public readonly record struct RouteServeKeys(long? FromCityKey, long? ToCityKey, bool FromPending = false, bool ToPending = false);
