using RentNearBy.Core.Models.Owner.Quote;

namespace RentNearBy.Tests.Stays.Quotation;

// S14 / Q4: the server pins the SHA-256 of the quotation terms text per version. The app test computes the hash of
// stay_quote_terms_strings.dart against the same fixture; a wording change without a version bump fails both gates.
public class QuoteTermsHashTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Stays", "Quotation", "Fixtures", name));

    [Fact]
    public void Current_version_is_one_and_has_a_pinned_hash()
    {
        Assert.Equal(1, QuoteTerms.CurrentVersion);
        Assert.True(QuoteTerms.Sha256.ContainsKey(QuoteTerms.CurrentVersion));
        Assert.Matches("^[0-9a-f]{64}$", QuoteTerms.Sha256[QuoteTerms.CurrentVersion]);
    }

    [Fact]
    public void Fixture_text_hashes_to_the_server_constant_and_the_shared_hash_file()
    {
        var text = Fixture("quote_terms_v1.txt");
        Assert.DoesNotContain("\r", text);
        Assert.False(text.EndsWith('\n'));
        Assert.Equal(QuoteTerms.Sha256[1], QuoteTerms.HashOf(text));
        Assert.Equal(QuoteTerms.Sha256[1], Fixture("quote_terms_v1.sha256").Trim());
    }

    [Fact]
    public void A_one_character_change_changes_the_hash()
    {
        var text = Fixture("quote_terms_v1.txt");
        Assert.NotEqual(QuoteTerms.Sha256[1], QuoteTerms.HashOf(text + " "));
        Assert.NotEqual(QuoteTerms.Sha256[1], QuoteTerms.HashOf(text.Replace("\n", "\r\n")));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    public void Only_the_current_version_is_known(int version, bool known) => Assert.Equal(known, QuoteTerms.IsKnown(version));
}
