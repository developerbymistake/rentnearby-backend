namespace RentNearBy.Core.Models.Owner.Quote;

// Typed refusals of /api/v1/stays/me/quotation (BUILD_PLAN 4.2). Messages are static: no request value is ever echoed.
public static class QuoteErrors
{
    public const string ValidationError = "ValidationError";
    public const string GstinMismatch = "QuoteGstinMismatch";
    public const string TermsVersionUnknown = "QuoteTermsVersionUnknown";
    public const string StayNotFound = "StayNotFound";
    public const string ImageMissing = "QuoteImageMissing";
    public const string StayNotApproved = "StayNotApproved";
    public const string TermsNotAccepted = "QuoteTermsNotAccepted";
    public const string MonthLimit = "QuotationMonthLimit";
    public const string BankUnavailable = "QuoteBankUnavailable";
    public const string PhotoBusy = "PhotoBusy";
    public const string PhotoInvalid = "PhotoInvalid";
    public const string PhotoTooLarge = "PhotoTooLarge";

    private static readonly Dictionary<string, (int Status, string Message)> Table = new()
    {
        [ValidationError] = (400, "Please check the highlighted fields."),
        [GstinMismatch] = (400, "This GST number does not match the one on your stay."),
        [TermsVersionUnknown] = (400, "Update the app to continue."),
        [StayNotFound] = (404, "Set up your stay first."),
        [ImageMissing] = (404, "No image saved yet."),
        [StayNotApproved] = (409, "Quotations open once your stay is approved."),
        [TermsNotAccepted] = (409, "Please accept the quotation terms first."),
        [MonthLimit] = (409, "You have used all quotations for this month."),
        [BankUnavailable] = (503, "Bank details cannot be saved right now. Try again later."),
        [PhotoBusy] = (503, "Busy right now. Try again in a few seconds."),
    };

    public static int StatusOf(string type) => Table.TryGetValue(type, out var e) ? e.Status : 400;

    public static string MessageOf(string type) => Table.TryGetValue(type, out var e) ? e.Message : "Invalid request.";
}
