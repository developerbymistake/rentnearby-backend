using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPaymentTransactionRepository
{
    Task AddAsync(PaymentTransaction transaction);
    Task<PaymentTransaction?> GetByIdAsync(Guid id);
    Task<PaymentTransaction?> GetByRazorpayOrderIdAsync(string orderId);
    Task<IEnumerable<PaymentTransaction>> GetByUserIdAsync(Guid userId);
    Task SaveAsync();
}
