namespace RentNearBy.Core.DTOs.Owner.Quote;

// GET /stays/me/quotation (ARCHITECTURE 3.1). Never carries the full account number or UPI.
public sealed record QuoteHubDto(
    QuoteHubStayDto Stay,
    string? PreparedBy,
    QuoteProfileDto? Profile,
    QuoteDefaultsDto Defaults,
    QuoteTermsStateDto Terms,
    QuoteUsageDto Usage,
    QuoteNextDto Next,
    bool BankStorage,
    bool BankUnreadable,
    int MinTemplateVersion,
    bool PreviewWatermark,
    int MaxFreePerWindow);

public sealed record QuoteHubStayDto(string? Name, bool Approved, string? PendingName, string? LastNameResult, bool HasGstin);

public sealed record QuoteProfileDto(
    string? Slogan,
    string? Address,
    string? Phone,
    string? Email,
    string? Gstin,
    QuoteBankSummaryDto Bank,
    string? TermsText,
    string? CancellationText,
    bool HasLogo,
    bool HasSignature,
    int AssetsVersion,
    DateTime UpdatedAt);

public sealed record QuoteBankSummaryDto(bool Has, string? Bank, string? Branch, string? AccountLast4, string? Ifsc, string? UpiMasked)
{
    public static readonly QuoteBankSummaryDto None = new(false, null, null, null, null, null);
}

public sealed record QuoteDefaultsDto(string? Address, string? Phone, string? Upi);

public sealed record QuoteTermsStateDto(int CurrentVersion, int? AcceptedVersion, DateTime? AcceptedAt);

public sealed record QuoteUsageDto(int Used, int Limit, DateTime ResetsAt, bool CanUpgrade);

public sealed record QuoteNextDto(int Quotation, int Proforma);

// GET /profile/bank: the full block, owner only, no-store.
public sealed record QuoteBankDto(string? Holder, string? AccountNo, string? Ifsc, string? Bank, string? Branch, string? Upi);

public sealed record QuoteAssetsDto(int AssetsVersion);

public sealed record QuoteTermsAcceptedDto(int AcceptedVersion, DateTime AcceptedAt);

public sealed record QuoteReserveDto(
    int Number,
    string Display,
    string Kind,
    bool Counted,
    DateTime FreeUntil,
    int FreeLeft,
    DateTime IssuedAt,
    QuoteUsageDto Usage,
    string? BusinessName,
    int AssetsVersion);

public sealed record QuoteNameChangeDto(string? PendingName);

// 409 QuotationMonthLimit data (shape of the social cap data plus the plan key).
public sealed record QuoteLimitDataDto(string Key, int Limit, int Used, bool CanUpgrade, DateTime ResetsAt);
