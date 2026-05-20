using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PlotPaymentFeatureRepository(ApplicationDbContext context) : IPlotPaymentFeatureRepository
{
    public async Task<PlotPaymentFeature?> GetAsync()
        => await context.PlotPaymentFeatures.FirstOrDefaultAsync();

    public async Task AddAsync(PlotPaymentFeature feature)
        => await context.PlotPaymentFeatures.AddAsync(feature);

    public Task UpdateAsync(PlotPaymentFeature feature)
    {
        context.PlotPaymentFeatures.Update(feature);
        return Task.CompletedTask;
    }
}
