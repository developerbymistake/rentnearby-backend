using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IInquiryStatusHistoryRepository : IRepository<InquiryStatusHistory>
{
    Task<IEnumerable<InquiryStatusHistory>> GetByInquiryIdAsync(Guid inquiryId);
}
