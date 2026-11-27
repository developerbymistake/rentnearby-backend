namespace RentNearBy.Core.Models.Owner.Quote;

// Stay quotation server constants (ARCHITECTURE 3, 4.4, BUILD_PLAN 4.4). D-2 / D-3 are one constant each, no app release.
public static class QuoteRules
{
    public const int MaxFreePerWindow = 5;
    public const bool PreviewWatermark = true;
    public const int MinTemplateVersion = 1;
    public static readonly TimeSpan FreeWindow = TimeSpan.FromHours(24);
    public const int IssueRetentionDays = 180;

    public const int SloganMax = 80;
    public const int AddressMax = 240;
    public const int AddressMaxLines = 4;
    public const int PhoneMax = 20;
    public const int EmailMax = 120;
    public const int GstinLength = 15;
    public const int TermsMax = 1200;
    public const int CancellationMax = 800;
    public const int ImagePathMax = 300;
    public const int BankTextMax = 60;
    public const int AppVersionMax = 20;
    public const int NameChangeNoteMax = 200;

    // Images: re-encoded JPEG; refused above the hard cap at the lowest quality.
    public const int ImageTargetBytes = 120 * 1024;
    public const int ImageHardMaxBytes = 200 * 1024;
    public const int ImageMaxPixels = 24_000_000;
    public const int LogoBox = 512;
    public const int SignatureBoxWidth = 900;
    public const int SignatureBoxHeight = 450;

    public const int NumberPad = 4;
    public const short ReserveBatchTimeoutSeconds = 5;
}

// Wire values of the document type; the stored smallint is the index.
public static class QuoteKinds
{
    public const string Quotation = "Quotation";
    public const string Proforma = "Proforma";

    public const short QuotationCode = 0;
    public const short ProformaCode = 1;

    public static bool TryCode(string? kind, out short code)
    {
        code = kind switch { Quotation => QuotationCode, Proforma => ProformaCode, _ => (short)-1 };
        return code >= 0;
    }

    public static string NameOf(short code) => code == ProformaCode ? Proforma : Quotation;

    public static string PrefixOf(short code) => code == ProformaCode ? "PI-" : "Q-";

    // Q-0042 / PI-0007; a number above 9999 just grows.
    public static string Display(short code, int number) => PrefixOf(code) + number.ToString().PadLeft(QuoteRules.NumberPad, '0');
}

// Image kinds of the master: path segment and wire value of GET /image/{kind}.
public enum QuoteImageKind { Logo, Signature }

public static class QuoteImageKinds
{
    public const string Logo = "logo";
    public const string Signature = "signature";

    public static bool TryParse(string? value, out QuoteImageKind kind)
    {
        switch (value)
        {
            case Logo: kind = QuoteImageKind.Logo; return true;
            case Signature: kind = QuoteImageKind.Signature; return true;
            default: kind = default; return false;
        }
    }

    public static string Wire(QuoteImageKind kind) => kind == QuoteImageKind.Logo ? Logo : Signature;
}

// Bank intent of PUT /profile: the form can save without ever loading the bank block.
public static class QuoteBankModes
{
    public const string Keep = "Keep";
    public const string Set = "Set";
    public const string Clear = "Clear";

    public static readonly string[] All = [Keep, Set, Clear];
}

// Own buckets of the quotation routes (ARCHITECTURE 9.2). Taken before the handler and NEVER given back, so a loop of refused
// calls (400 / 409) still ends in 429.
public static class QuoteRateLimits
{
    public static OwnerRateLimit Photo(Guid userId) => new($"quote:photo:{userId}", 10, TimeSpan.FromHours(1));
    public static OwnerRateLimit Reserve(Guid userId) => new($"quote:reserve:{userId}", 30, TimeSpan.FromHours(1));
    public static OwnerRateLimit NameChange(Guid userId) => new($"quote:name:{userId}", 3, TimeSpan.FromDays(1));
}
