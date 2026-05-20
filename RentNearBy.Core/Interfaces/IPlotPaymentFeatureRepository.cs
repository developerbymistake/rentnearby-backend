using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPlotPaymentFeatureRepository
{
    Task<PlotPaymentFeature?> GetAsync();
    Task AddAsync(PlotPaymentFeature feature);
    Task UpdateAsync(PlotPaymentFeature feature);
}
