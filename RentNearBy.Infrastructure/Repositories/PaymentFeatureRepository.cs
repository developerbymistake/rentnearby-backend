using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PaymentFeatureRepository(ApplicationDbContext context) : IPaymentFeatureRepository
{
    public async Task<PaymentFeature?> GetAsync()
        => await context.PaymentFeatures.FirstOrDefaultAsync();

    public async Task AddAsync(PaymentFeature feature)
    {
        await context.PaymentFeatures.AddAsync(feature);
    }

    public async Task UpdateAsync(PaymentFeature feature)
    {
        var existing = await GetAsync();
        if (existing != null)
        {
            existing.IsEnabled = feature.IsEnabled;
            existing.FreePlanDays = feature.FreePlanDays;
            existing.FreePlanRoomLimit = feature.FreePlanRoomLimit;
            existing.PaidPlanPrice = feature.PaidPlanPrice;
            existing.PaidPlanDays = feature.PaidPlanDays;
            existing.PaidPlanRoomLimit = feature.PaidPlanRoomLimit;
            existing.FreeListingDaysWhenDisabled = feature.FreeListingDaysWhenDisabled;
            existing.UpdatedAt = DateTime.UtcNow;
            context.PaymentFeatures.Update(existing);
        }
    }

    public async Task SaveAsync()
        => await context.SaveChangesAsync();
}
