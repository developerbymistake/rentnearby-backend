using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Core.Interfaces;

public interface ICoinPackPurchaseService
{
    Task<CreatePaymentOrderResponse> CreateOrderAsync(Guid userId, Guid coinPackId);
    Task<CoinPackPurchaseVerifyResponse> VerifyAndCreditAsync(Guid userId, VerifyPaymentRequest request, bool skipSignatureCheck = false);
}
