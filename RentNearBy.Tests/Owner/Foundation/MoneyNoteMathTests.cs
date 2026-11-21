using System.Text.Json;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Foundation;

// Doc 6.2 (every row) and R-12: the shared money note vectors when the app foundation has written them.
public class MoneyNoteMathTests
{
    private static MoneyNote N(int? final, int? advance, bool paid = false) => new(final, advance, paid);

    [Theory]
    [InlineData(null, null, false, null)]
    [InlineData(6000, null, false, null)]
    [InlineData(6000, 2000, false, null)]
    [InlineData(null, 2000, false, null)]
    [InlineData(6000, 6000, false, null)]
    [InlineData(6000, 0, false, null)]
    [InlineData(6000, null, true, null)]
    [InlineData(6000, 7000, false, MoneyNoteMath.AdvanceAboveFinal)]
    [InlineData(null, null, true, MoneyNoteMath.FinalFirst)]
    [InlineData(null, 500, true, MoneyNoteMath.FinalFirst)]
    [InlineData(0, null, false, MoneyNoteMath.AmountRange)]
    [InlineData(9_900_001, null, false, MoneyNoteMath.AmountRange)]
    [InlineData(null, -1, false, MoneyNoteMath.AmountRange)]
    [InlineData(null, 9_900_001, false, MoneyNoteMath.AmountRange)]
    [InlineData(9_900_000, 9_900_000, true, null)]
    [InlineData(1, 0, false, null)]
    public void Validate_follows_doc_6_2(int? final, int? advance, bool paid, string? expected)
        => Assert.Equal(expected, MoneyNoteMath.Validate(N(final, advance, paid)));

    [Theory]
    [InlineData(6000, 2000, false, 2000, 4000, MoneyChips.ToCollect, 4000)]
    [InlineData(6000, null, false, 0, 6000, MoneyChips.ToCollect, 6000)]
    [InlineData(6000, 2000, true, 6000, 0, MoneyChips.PaidInFull, 6000)]
    [InlineData(6000, 6000, false, 6000, 0, MoneyChips.PaidInFull, 6000)]
    [InlineData(null, 2000, false, 2000, null, MoneyChips.Received, 2000)]
    [InlineData(null, 0, false, 0, null, MoneyChips.None, 0)]
    [InlineData(null, null, false, 0, null, MoneyChips.None, 0)]
    public void Received_balance_and_chip(int? final, int? advance, bool paid, int received, int? balance, string chip, int chipAmount)
    {
        var v = MoneyNoteMath.View(N(final, advance, paid));
        Assert.Equal(received, v.Received);
        Assert.Equal(balance, v.Balance);
        Assert.Equal(chip, v.Chip);
        Assert.Equal(chipAmount, v.ChipAmount);
        Assert.Equal(received, MoneyNoteMath.Received(N(final, advance, paid)));
        Assert.Equal(balance, MoneyNoteMath.Balance(N(final, advance, paid)));
    }

    [Fact]
    public void Sql_forms_name_the_same_columns_and_rule()
    {
        var received = MoneyNoteMath.SqlReceived("e");
        Assert.Equal("(CASE WHEN e.\"PaidInFull\" AND e.\"FinalAmount\" IS NOT NULL THEN e.\"FinalAmount\" ELSE COALESCE(e.\"AdvanceReceived\", 0) END)", received);
        Assert.Equal($"(CASE WHEN e.\"FinalAmount\" IS NULL THEN NULL ELSE GREATEST(0, e.\"FinalAmount\" - {received}) END)", MoneyNoteMath.SqlBalance("e"));
    }

    [Fact]
    public void Error_type_message_is_the_advance_line()
        => Assert.Equal(MoneyNoteMath.AdvanceAboveFinal, OwnerErrorTypes.MessageOf(OwnerErrorTypes.MoneyNoteInvalid));

    // R-12: every case of the shared money note vector file (written by the app foundation from doc 6.2, mirrored here).
    [Fact]
    public void Shared_money_note_vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Owner", "Vectors", "money_note_vectors.json");
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        static int? I(JsonElement c, string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        var cases = root.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 10);
        Assert.All(cases, c =>
        {
            var note = N(I(c, "finalAmount"), I(c, "advanceReceived"), c.GetProperty("paidInFull").GetBoolean());
            var e = c.GetProperty("expected");
            var v = MoneyNoteMath.View(note);
            var name = c.GetProperty("name").GetString();
            Assert.True(e.GetProperty("received").GetInt32() == v.Received, $"{name}: received");
            Assert.True(I(e, "balance") == v.Balance, $"{name}: balance");
            Assert.True(e.GetProperty("chip").GetString() == v.Chip, $"{name}: chip");
            Assert.True(e.GetProperty("chipAmount").GetInt32() == v.ChipAmount, $"{name}: chipAmount");
            var error = e.GetProperty("error");
            Assert.True((error.ValueKind == JsonValueKind.Null ? null : error.GetString()) == MoneyNoteMath.ErrorKey(MoneyNoteMath.Validate(note)), $"{name}: error");
        });
    }

    [Fact]
    public void Test_copy_equals_the_shared_money_note_file_when_present()
    {
        var shared = "F:/Dhasu/Buddy_resume/owner_build/vectors/money_note_vectors.json";
        if (!File.Exists(shared)) return;
        Assert.Equal(File.ReadAllText(shared).ReplaceLineEndings(),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Owner", "Vectors", "money_note_vectors.json")).ReplaceLineEndings());
    }
}
