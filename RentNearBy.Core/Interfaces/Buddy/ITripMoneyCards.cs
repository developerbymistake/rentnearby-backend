using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Core.Interfaces.Buddy;

public interface ITripMoneyCards
{
    // 2 statements for any number of trips: state rows + my share sums. Missing trip = no expenses.
    Task<IReadOnlyDictionary<Guid, TripMoneyCard>> GetCardsAsync(IReadOnlyCollection<Guid> tripIds, Guid userId, CancellationToken ct = default);

    // LoadManyAsync + GetTravellersManyAsync: my settle net, hasShare, shared expense count, first receiver.
    Task<IReadOnlyDictionary<Guid, TripMoneyStanding>> GetStandingsAsync(IReadOnlyCollection<Guid> tripIds, Guid userId, CancellationToken ct = default);
}
