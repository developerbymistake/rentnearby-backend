using RentNearBy.Core.Utils;

namespace RentNearBy.Tests.Districts;

public class SearchKeysTests
{
    [Theory]
    [InlineData("Dwārāhāt", "dwarahat")]
    [InlineData("Hēm Kund", "hem kund")]
    [InlineData("St. Mary’s Church", "st mary s church")]
    [InlineData("Kalladikuttai - கல்லடிக்குட்டை", "kalladikuttai")]
    [InlineData("Rani Sui Lake 360° view point", "rani sui lake 360 view point")]
    [InlineData("  Sri   Bharat Mandir ", "sri bharat mandir")]
    [InlineData("Tamil ்", "tamil")]
    [InlineData("ＡＢ City", "ab city")]
    [InlineData("Rishikesh", "rishikesh")]
    [InlineData("---", "")]
    [InlineData("", "")]
    [InlineData("İstanbul", "istanbul")]
    [InlineData("नैनीताल", "")]
    public void Fold_MatchesAtlasVectors(string input, string expected)
        => Assert.Equal(expected, SearchKeys.Fold(input));

    [Fact]
    public void Fold_Null_Empty()
        => Assert.Equal(string.Empty, SearchKeys.Fold(null));

    [Theory]
    [InlineData("ø")]
    [InlineData("ł")]
    [InlineData("æ")]
    [InlineData("ß")]
    public void Fold_LettersWithoutDecomposition_AreSeparators(string input)
        => Assert.Equal(string.Empty, SearchKeys.Fold(input));

    [Theory]
    [InlineData("Dwārāhāt")]
    [InlineData("St. Mary’s Church")]
    [InlineData("  Sri   Bharat Mandir ")]
    [InlineData("ＡＢ City")]
    public void Fold_IsIdempotent(string input)
    {
        var once = SearchKeys.Fold(input);
        Assert.Equal(once, SearchKeys.Fold(once));
    }
}
