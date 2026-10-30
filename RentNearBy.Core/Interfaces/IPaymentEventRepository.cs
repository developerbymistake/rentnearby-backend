using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPaymentEventRepository
{
    // INSERT ... ON CONFLICT ("EventId") DO NOTHING; false = duplicate delivery.
    Task<bool> TryInsertAsync(PaymentEvent paymentEvent);
    Task SetOutcomeAsync(string eventId, string outcome);
    Task<List<PaymentEvent>> GetByOrderIdAsync(string razorpayOrderId, int limit);
    Task<List<PaymentEvent>> GetByPlayTokenAsync(string purchaseToken, int limit);
    // Events of a Play purchase: by token, or by order id (chargeback-review events carry no token).
    Task<List<PaymentEvent>> GetByPlayTokenOrOrderAsync(string? purchaseToken, string? playOrderId, int limit);
}
