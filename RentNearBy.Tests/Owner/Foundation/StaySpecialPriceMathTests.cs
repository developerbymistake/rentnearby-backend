using System.Text.Json;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Foundation;

// Contract 8.2 and AU-13: every case of vectors/special_price_vectors.json (version 2). The app runs the same file.
public class StaySpecialPriceMathTests
{
    private static readonly string VectorPath = Path.Combine(AppContext.BaseDirectory, "Owner", "Vectors", "special_price_vectors.json");
    private static readonly Lazy<JsonElement> Vectors = new(() => JsonDocument.Parse(File.ReadAllText(VectorPath)).RootElement);

    private static JsonElement Group(string name) => Vectors.Value.GetProperty(name);

    private static DateOnly D(JsonElement e) => DateOnly.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    // Only active rules reach the function (paused and plan-paused rules are filtered by the caller).
    private static List<PriceRule> Rules(JsonElement rules) => rules.EnumerateArray()
        .Where(r => !(r.TryGetProperty("paused", out var p) && p.GetBoolean()) && !(r.TryGetProperty("pausedByPlan", out var q) && q.GetBoolean()))
        .Select(r => new PriceRule(D(r.GetProperty("from")), D(r.GetProperty("to")), r.GetProperty("direction").GetString() == "Lower", r.GetProperty("percent").GetInt32()))
        .ToList();

    private static int? NullableInt(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();

    [Fact]
    public void Vector_file_is_version_2_with_every_group()
    {
        Assert.Equal(2, Vectors.Value.GetProperty("version").GetInt32());
        Assert.Equal(20, Group("adjust").GetArrayLength());
        Assert.Equal(12, Group("totals").GetArrayLength());
        foreach (var g in new[] { "perNight", "chip", "fullOnDates", "partyQuote", "stayCardChip30" })
            Assert.True(Group(g).GetArrayLength() >= 3, g);
    }

    [Fact]
    public void Test_copy_equals_the_shared_vector_file_when_present()
    {
        var shared = "F:/Dhasu/Buddy_resume/owner_build/vectors/special_price_vectors.json";
        if (!File.Exists(shared)) return;
        Assert.Equal(File.ReadAllText(shared).ReplaceLineEndings(), File.ReadAllText(VectorPath).ReplaceLineEndings());
    }

    [Fact]
    public void Adjust_matches_every_vector()
        => Assert.All(Group("adjust").EnumerateArray(), c => Assert.Equal(c.GetProperty("expected").GetInt32(),
            StaySpecialPriceMath.Adjust(c.GetProperty("base").GetInt32(), c.GetProperty("direction").GetString() == "Lower", c.GetProperty("percent").GetInt32())));

    [Fact]
    public void Totals_and_night_prices_match_every_vector()
    {
        Assert.All(Group("totals").EnumerateArray(), c =>
        {
            var basePrice = c.GetProperty("base").GetInt32();
            var checkIn = D(c.GetProperty("checkIn"));
            var nights = D(c.GetProperty("checkOut")).DayNumber - checkIn.DayNumber;
            var rules = Rules(c.GetProperty("rules"));
            Assert.Equal(c.GetProperty("total").GetInt32(), StaySpecialPriceMath.Total(basePrice, checkIn, nights, rules));
            foreach (var n in c.GetProperty("nights").EnumerateArray())
                Assert.Equal(n.GetProperty("price").GetInt32(), StaySpecialPriceMath.PriceOn(basePrice, D(n.GetProperty("date")), rules));
        });
    }

    [Fact]
    public void Per_night_matches_every_vector()
        => Assert.All(Group("perNight").EnumerateArray(), c => Assert.Equal(c.GetProperty("expected").GetInt32(),
            StaySpecialPriceMath.PerNight(c.GetProperty("total").GetInt32(), c.GetProperty("nights").GetInt32())));

    [Fact]
    public void Chip_matches_every_vector()
        => Assert.All(Group("chip").EnumerateArray(), c => Assert.Equal(NullableInt(c.GetProperty("expectedPercent")),
            StaySpecialPriceMath.LowerPercentFor(D(c.GetProperty("checkIn")), c.GetProperty("nights").GetInt32(), Rules(c.GetProperty("rules")))));

    [Fact]
    public void Full_on_dates_matches_every_vector()
        => Assert.All(Group("fullOnDates").EnumerateArray(), c => Assert.Equal(c.GetProperty("expected").GetBoolean(),
            StaySpecialPriceMath.TouchesBlock(D(c.GetProperty("checkIn")), c.GetProperty("nights").GetInt32(),
                c.GetProperty("blocks").EnumerateArray().Select(b => (D(b.GetProperty("from")), D(b.GetProperty("to")))).ToList())));

    [Fact]
    public void Party_quote_matches_every_vector()
        => Assert.All(Group("partyQuote").EnumerateArray(), c =>
        {
            var checkIn = D(c.GetProperty("checkIn"));
            var nights = c.GetProperty("nights").GetInt32();
            var rules = Rules(c.GetProperty("rules"));
            var total = c.GetProperty("rooms").EnumerateArray()
                .Sum(r => r.GetProperty("units").GetInt32() * StaySpecialPriceMath.Total(r.GetProperty("basePrice").GetInt32(), checkIn, nights, rules));
            Assert.Equal(c.GetProperty("expectedTotal").GetInt32(), total);
            Assert.Equal(c.GetProperty("expectedPerNight").GetInt32(), StaySpecialPriceMath.PerNight(total, nights));
        });

    [Fact]
    public void Stay_card_chip_matches_every_vector()
        => Assert.All(Group("stayCardChip30").EnumerateArray(), c => Assert.Equal(NullableInt(c.GetProperty("expected")),
            StaySpecialPriceMath.CardChipPercent(D(c.GetProperty("today")), Rules(c.GetProperty("rules")))));

    [Fact]
    public void Check_out_day_is_not_a_night_and_first_and_last_nights_count()
    {
        List<PriceRule> rules = [new(new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 12), false, 20)];
        Assert.Equal(6000, StaySpecialPriceMath.Total(3000, new DateOnly(2026, 11, 4), 2, rules));
        Assert.Equal(3600, StaySpecialPriceMath.PriceOn(3000, new DateOnly(2026, 11, 6), rules));
        Assert.Equal(3600, StaySpecialPriceMath.PriceOn(3000, new DateOnly(2026, 11, 12), rules));
        Assert.Equal(3000, StaySpecialPriceMath.PriceOn(3000, new DateOnly(2026, 11, 13), rules));
    }

    [Fact]
    public void Adjust_clamps_and_per_night_guards_zero_nights()
    {
        Assert.Equal(100, StaySpecialPriceMath.Adjust(100, true, 50));
        Assert.Equal(9_900_000, StaySpecialPriceMath.Adjust(9_900_000, false, 100));
        Assert.Equal(500, StaySpecialPriceMath.PerNight(500, 0));
        Assert.False(StaySpecialPriceMath.TouchesBlock(new DateOnly(2026, 1, 1), 0, [(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1))]));
        Assert.Equal("15% off", StaySpecialPriceMath.ChipText(15));
    }
}
