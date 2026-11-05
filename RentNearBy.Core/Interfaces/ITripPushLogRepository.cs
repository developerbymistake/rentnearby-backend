using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ITripPushLogRepository
{
    // Inserts and saves at once; false when DedupeKey already exists.
    Task<bool> TryInsertAsync(TripPushLog row);
    Task<int> PurgeOlderThanAsync(DateTime cutoffUtc);
    // Logged keys of one enquiry that start with keyPrefix (ordinal).
    Task<List<string>> GetKeysAsync(Guid enquiryId, string keyPrefix);
}
