using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ITripOfferRepository : IRepository<TripOffer>
{
    // Tracked, newest first.
    Task<IReadOnlyList<TripOffer>> GetByEnquiryAsync(Guid enquiryId);
    // AsNoTracking, newest first per enquiry.
    Task<Dictionary<Guid, List<TripOffer>>> GetByEnquiriesAsync(IReadOnlyCollection<Guid> enquiryIds);
    // Tracked.
    Task<TripOffer?> GetPendingAsync(Guid enquiryId);
    // AsNoTracking.
    Task<IReadOnlyList<TripOffer>> GetExpiredPendingAsync(DateTime nowUtc, int take);
}
