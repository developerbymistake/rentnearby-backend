using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPaymentFeatureRepository
{
    Task<PaymentFeature?> GetAsync();
    Task UpdateAsync(PaymentFeature feature);
    Task SaveAsync();
}
