using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IEnquiryStatusHistoryRepository : IRepository<EnquiryStatusHistory>
{
    Task<IEnumerable<EnquiryStatusHistory>> GetByEnquiryIdAsync(Guid enquiryId);
}
