using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PlotTypeRepository : Repository<PlotType>, IPlotTypeRepository
{
    public PlotTypeRepository(ApplicationDbContext context) : base(context) { }
}
