namespace RentNearBy.Core.Interfaces;

// Provider = PaymentProviders.Play | PaymentProviders.Razorpay (gateway for new orders).
public sealed record PaymentFeatureState(bool Enabled, int FreeDays, int? FreeModeListingLimit, string Provider);

// Cached payment kill switch (IMemoryCache key config_payment_feature). A missing row fails closed
// (Enabled = true), never silently making everything free.
public interface IPaymentFeatureReader
{
    public const string CacheKey = "config_payment_feature";

    Task<PaymentFeatureState> GetAsync();
    void Invalidate();
}
