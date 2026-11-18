namespace RentNearBy.Core.Interfaces.TaxiRequests;

// Wakes the matcher for a city (a burst collapses to one pass) or for the cities of a driver who just became free.
public interface IMatcherSignal
{
    void MarkDirty(long cityKey);
    void MarkDriverFreed(Guid driverListingId);
}

public interface IDueActionsSignal
{
    void Wake();
}

// Realtime hint for the traveller's Status screen; the event carries no data the client trusts over a GET.
public interface ITaxiRequestEvents
{
    Task RequestChangedAsync(Guid requestId, Guid tripId, Guid travellerUserId, long version, string state);
}

// Ack tokens of ring pushes: base64url of the first 16 bytes of HMAC-SHA256(server key, offer id).
public interface ITaxiAckTokens
{
    string Create(Guid offerId);
    bool IsValid(Guid offerId, string? token);
}
