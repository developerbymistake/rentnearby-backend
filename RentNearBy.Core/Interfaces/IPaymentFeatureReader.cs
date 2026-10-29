namespace RentNearBy.Core.Interfaces;

public sealed record PaymentFeatureState(bool Enabled, int FreeDays, int? FreeModeListingLimit);

// Cached payment kill switch (IMemoryCache key config_payment_feature). A missing row fails closed
// (Enabled = true), never silently making everything free.
public interface IPaymentFeatureReader
{
    public const string CacheKey = "config_payment_feature";

    Task<PaymentFeatureState> GetAsync();
    void Invalidate();
}
