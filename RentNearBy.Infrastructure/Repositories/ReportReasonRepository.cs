using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class ReportReasonRepository : Repository<ReportReason>, IReportReasonRepository
{
    public ReportReasonRepository(ApplicationDbContext context) : base(context) { }
}
