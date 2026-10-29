using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPaymentEventRepository
{
    // INSERT ... ON CONFLICT ("EventId") DO NOTHING; false = duplicate delivery.
    Task<bool> TryInsertAsync(PaymentEvent paymentEvent);
    Task SetOutcomeAsync(string eventId, string outcome);
    Task<List<PaymentEvent>> GetByOrderIdAsync(string razorpayOrderId, int limit);
}
