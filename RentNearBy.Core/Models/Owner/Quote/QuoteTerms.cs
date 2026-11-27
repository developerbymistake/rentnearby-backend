using System.Security.Cryptography;
using System.Text;

namespace RentNearBy.Core.Models.Owner.Quote;

// Quotation terms (ARCHITECTURE 7, S14). The text lives in the app strings; the server pins its SHA-256 per version so a wording
// change without a version bump fails both gates (shared fixture quote_terms_v1.txt / .sha256). Hash = SHA-256 of the UTF-8
// text, lines joined with "\n", no trailing newline, lower-case hex.
public static class QuoteTerms
{
    public const short CurrentVersion = 1;

    public static readonly IReadOnlyDictionary<short, string> Sha256 = new Dictionary<short, string>
    {
        [1] = "efb488a65f466a9dab05a85d92fcad83df178a5aa84d06f8105076144618d816",
    };

    public static bool IsKnown(int version) => version == CurrentVersion;

    public static string HashOf(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
