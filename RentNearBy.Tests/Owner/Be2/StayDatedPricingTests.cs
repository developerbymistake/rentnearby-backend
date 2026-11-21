using System.Text.Json;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be2;

namespace RentNearBy.Tests.Owner.Be2;

// Contract 2.5 T1 / T2, 8.2..8.4 and the AU-13 vector groups run through the read path (lane BE2).
public class StayDatedPricingTests
{
    private static readonly DateOnly Today = new(2026, 6, 1);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly StayDatedPricing Pricing = new();
    private static readonly string VectorPath = Path.Combine(AppContext.BaseDirectory, "Owner", "Vectors", "special_price_vectors.json");
    private static readonly Lazy<JsonElement> Vectors = new(() => JsonDocument.Parse(File.ReadAllText(VectorPath)).RootElement);

    private static StayNearbyDto Stay(int price = 3000, List<StayRuleRow>? rules = null, List<StayBlockRow>? blocks = null) => new()
    {
        Id = Guid.NewGuid(), Name = "Lakeview Homestay", MinPricePerNight = price, DistanceKm = 0.4,
        Rooms = [new StayNearbyRoomDto { RoomName = "Deluxe", MaxGuests = 2, PricePerNight = price }, new StayNearbyRoomDto { RoomName = "Family Room", MaxGuests = 4, PricePerNight = price * 2 }],
        Rules = rules, Blocks = blocks,
    };

    private static StayRuleRow Rule(DateOnly from, DateOnly to, bool lower, int percent) => new() { From = from, To = to, Lower = lower, Percent = percent };

    private static DateOnly D(int month, int day) => new(2026, month, day);

    [Fact]
    public void No_check_in_returns_the_same_json_as_today_and_strips_the_cache_rows()
    {
        var plain = Stay();
        var withRows = Stay();
        withRows.Id = plain.Id;
        withRows.Rules = [Rule(D(6, 10), D(6, 12), true, 15)];
        withRows.Blocks = [new StayBlockRow { From = D(6, 20), To = D(6, 21) }];
        var before = JsonSerializer.Serialize(plain, Web);

        var after = Pricing.Apply([withRows], null, 1, null, null, false, Today);

        Assert.Equal(before, JsonSerializer.Serialize(after[0], Web));
        Assert.Null(after[0].Rules);
        Assert.Null(after[0].Blocks);
    }

    [Fact]
    public void No_response_holds_rules_or_blocks_but_the_cache_copy_does()
    {
        var item = Stay(rules: [Rule(D(6, 10), D(6, 12), true, 15)], blocks: [new StayBlockRow { From = D(6, 20), To = D(6, 21) }]);
        var cacheJson = JsonSerializer.Serialize(new List<StayNearbyDto> { item });
        var cached = JsonSerializer.Deserialize<List<StayNearbyDto>>(cacheJson)!;
        Assert.Single(cached[0].Rules!);
        Assert.Single(cached[0].Blocks!);

        var response = JsonSerializer.Serialize(Pricing.Apply(cached, D(6, 10), 2, null, null, false, Today), Web);
        Assert.DoesNotContain("\"rules\"", response);
        Assert.DoesNotContain("\"blocks\"", response);
        Assert.Contains("\"datesPerNight\"", response);
    }

    [Fact]
    public void Lower_rule_on_part_of_the_nights_gives_the_dated_price_and_the_chip()
    {
        // Nights 18, 19, 20 Dec; 15% off on the 20th only.
        var item = Stay(1800, [Rule(D(12, 20), D(12, 25), true, 15)]);
        Pricing.Apply([item], D(12, 18), 3, null, null, false, D(12, 1));

        Assert.Equal(1800, item.BasePerNight);
        Assert.Equal(1800 + 1800 + StaySpecialPriceMath.Adjust(1800, true, 15), item.DatesTotal);
        Assert.Equal(StaySpecialPriceMath.PerNight(item.DatesTotal!.Value, 3), item.DatesPerNight);
        Assert.Equal(new PriceChipDto("Off", "15% off"), item.PriceChip);
        Assert.False(item.FullOnDates);
        Assert.Equal(1800, item.MinPricePerNight);
    }

    [Fact]
    public void Higher_rule_shows_only_the_price_never_a_chip()
    {
        var item = Stay(3000, [Rule(D(11, 6), D(11, 12), false, 20)]);
        Pricing.Apply([item], D(11, 6), 2, null, null, false, D(11, 1));
        Assert.Equal(7200, item.DatesTotal);
        Assert.Equal(3600, item.DatesPerNight);
        Assert.Equal(3000, item.BasePerNight);
        Assert.Null(item.PriceChip);
    }

    [Theory]
    [InlineData(8, 3, true)]   // nights 8, 9, 10: touches the first blocked night
    [InlineData(12, 2, true)]  // nights 12, 13: touches the last blocked night
    [InlineData(7, 3, false)]  // nights 7, 8, 9: check-out on the 10th, the first blocked night
    [InlineData(13, 2, false)] // after the block
    public void Full_on_dates_follows_the_nights(int checkInDay, int nights, bool full)
    {
        var item = Stay(blocks: [new StayBlockRow { From = D(12, 10), To = D(12, 12) }]);
        Pricing.Apply([item], D(12, checkInDay), nights, null, null, false, D(12, 1));
        Assert.Equal(full, item.FullOnDates);
    }

    [Fact]
    public void Ended_rules_and_blocks_are_ignored_by_the_date_check()
    {
        var item = Stay(3000, [Rule(D(5, 20), D(5, 31), true, 30)], [new StayBlockRow { From = D(5, 25), To = D(5, 31) }]);
        Pricing.Apply([item], D(6, 1), 1, null, null, false, Today);
        Assert.Equal(3000, item.DatesTotal);
        Assert.Null(item.PriceChip);
        Assert.False(item.FullOnDates);
    }

    [Fact]
    public void Party_quote_lines_take_the_dated_prices()
    {
        var item = Stay(3000, [Rule(D(11, 9), D(11, 12), false, 20)]);
        StayNearbyRooms.Apply([item], guests: 6, groundFloorOnly: false, roomsWanted: null, nights: 3);
        Assert.True(item.Fits);
        var baseTotal = item.QuoteTotal!.Value;
        var basePerNight = item.QuotePerNight!.Value;

        Pricing.Apply([item], D(11, 7), 3, 6, null, false, D(11, 1));

        Assert.Equal(basePerNight, item.BasePerNight);
        Assert.True(item.QuoteTotal > baseTotal);
        Assert.Equal(item.QuoteTotal, item.DatesTotal);
        Assert.Equal(StaySpecialPriceMath.PerNight(item.QuoteTotal!.Value, 3), item.QuotePerNight);
        Assert.Equal(item.QuotePerNight, item.DatesPerNight);
    }

    [Fact]
    public void Party_quote_vectors_match_through_the_list_pass()
        => Assert.All(Vectors.Value.GetProperty("partyQuote").EnumerateArray(), c =>
        {
            var checkIn = DateOnly.Parse(c.GetProperty("checkIn").GetString()!);
            var nights = c.GetProperty("nights").GetInt32();
            var lines = c.GetProperty("rooms").EnumerateArray().Select((r, i) => new StayQuoteLineDto
            {
                RoomName = $"Room {i}", Units = r.GetProperty("units").GetInt32(), Guests = 2, PricePerNight = r.GetProperty("basePrice").GetInt32(),
            }).ToList();
            var item = new StayNearbyDto
            {
                Id = Guid.NewGuid(), Fits = true, QuoteLines = lines,
                QuotePerNight = lines.Sum(l => l.Units * l.PricePerNight!.Value),
                Rules = VectorRules(c.GetProperty("rules")),
            };
            Pricing.Apply([item], checkIn, nights, 2, null, false, checkIn.AddDays(-30));
            Assert.Equal(c.GetProperty("expectedTotal").GetInt32(), item.DatesTotal);
            Assert.Equal(c.GetProperty("expectedPerNight").GetInt32(), item.DatesPerNight);
        });

    [Fact]
    public void Chip_vectors_match_through_the_list_pass()
        => Assert.All(Vectors.Value.GetProperty("chip").EnumerateArray(), c =>
        {
            var checkIn = DateOnly.Parse(c.GetProperty("checkIn").GetString()!);
            var item = Stay(3000, VectorRules(c.GetProperty("rules")));
            Pricing.Apply([item], checkIn, c.GetProperty("nights").GetInt32(), null, null, false, checkIn.AddDays(-60));
            var expected = c.GetProperty("expectedPercent");
            Assert.Equal(expected.ValueKind == JsonValueKind.Null ? null : PriceChipDto.Of(expected.GetInt32()), item.PriceChip);
        });

    [Fact]
    public void Full_on_dates_vectors_match_through_the_list_pass()
        => Assert.All(Vectors.Value.GetProperty("fullOnDates").EnumerateArray(), c =>
        {
            var checkIn = DateOnly.Parse(c.GetProperty("checkIn").GetString()!);
            var blocks = c.GetProperty("blocks").EnumerateArray()
                .Select(b => new StayBlockRow { From = DateOnly.Parse(b.GetProperty("from").GetString()!), To = DateOnly.Parse(b.GetProperty("to").GetString()!) })
                .ToList();
            // The block of "ended yesterday" is dropped by the request's today, like the SQL filter does.
            var item = Stay(blocks: blocks);
            Pricing.Apply([item], checkIn, c.GetProperty("nights").GetInt32(), null, null, false, checkIn.AddDays(-60));
            Assert.Equal(c.GetProperty("expected").GetBoolean(), item.FullOnDates);
        });

    [Fact]
    public void No_priced_room_leaves_the_dated_price_empty_but_still_answers_full_on_dates()
    {
        var item = new StayNearbyDto { Id = Guid.NewGuid(), Rooms = [new StayNearbyRoomDto { RoomName = "Dorm", MaxGuests = 1 }], Blocks = [new StayBlockRow { From = Today, To = Today }] };
        Pricing.Apply([item], Today, 1, null, null, false, Today);
        Assert.Null(item.DatesTotal);
        Assert.True(item.FullOnDates);
    }

    [Fact]
    public void Detail_rooms_get_the_price_for_the_dates_and_the_struck_base()
    {
        var rooms = new List<StayRoomDto>
        {
            new() { RoomName = "Deluxe", PricePerNight = 1800 },
            new() { RoomName = "Family Room", PricePerNight = 3600 },
        };
        var facts = new StayPriceFacts.StayPriceFactSet(Guid.NewGuid(), [],
            [new LabelledRule(new PriceRule(D(12, 1), D(12, 31), true, 15), "Winter")], []);
        var (min, chip, full) = StayDetailPricing.Fill(rooms, D(12, 18), 3, facts);

        Assert.Equal(1530, rooms[0].PriceForDates);
        Assert.Equal(1800, rooms[0].BasePrice);
        Assert.Equal(3060, rooms[1].PriceForDates);
        Assert.Equal(1530, min);
        Assert.Equal("15% off", chip!.Text);
        Assert.False(full);
    }

    [Fact]
    public void Detail_without_rules_shows_the_base_price_for_the_dates_and_no_chip()
    {
        var rooms = new List<StayRoomDto> { new() { RoomName = "Deluxe", PricePerNight = 1800 } };
        var (min, chip, full) = StayDetailPricing.Fill(rooms, D(12, 18), 2, null);
        Assert.Equal(1800, min);
        Assert.Null(chip);
        Assert.False(full);
    }

    [Fact]
    public void Detail_json_without_check_in_has_no_new_fields()
    {
        var json = JsonSerializer.Serialize(new StayPublicDto { Rooms = [new StayRoomDto { RoomName = "Deluxe", PricePerNight = 1800 }] }, Web);
        foreach (var name in new[] { "datesFromPerNight", "priceChip", "fullOnDates", "priceForDates", "basePrice" })
            Assert.DoesNotContain($"\"{name}\"", json);
    }

    [Fact]
    public void Card_chip_is_absent_from_the_browse_json_when_no_rule_runs()
    {
        var json = JsonSerializer.Serialize(new StayBrowseDto { Name = "Pine Nest" }, Web);
        Assert.DoesNotContain("priceChip", json);
        var withChip = JsonSerializer.Serialize(new StayBrowseDto { Name = "Lakeview", PriceChip = PriceChipDto.Of(15) }, Web);
        Assert.Contains("\"priceChip\":{\"kind\":\"Off\",\"text\":\"15% off\"}", withChip);
        Assert.Null(PriceChipDto.Of(null));
        Assert.Null(PriceChipDto.Of(0));
    }

    [Fact]
    public void Parsers_accept_the_nearby_json_and_ignore_broken_rows()
    {
        var rules = StayPriceFacts.ParseRules("""[{"from":"2026-11-06","to":"2026-11-12","lower":false,"percent":20}]""");
        Assert.Equal(20, Assert.Single(rules!).Percent);
        Assert.Null(StayPriceFacts.ParseRules(null));
        Assert.Null(StayPriceFacts.ParseRules("not json"));
        Assert.Null(StayPriceFacts.ParseBlocks("[]"));
        var blocks = StayPriceFacts.ParseBlocks("""[{"from":"2026-12-10","to":"2026-12-12"}]""");
        Assert.Equal(D(12, 12), Assert.Single(blocks!).To);
    }

    [Fact]
    public void Facts_rows_parse_rooms_rules_with_labels_and_blocks()
    {
        var facts = StayPriceFacts.ToFacts(new StayPriceFacts.StayPriceFactsRow
        {
            StayId = Guid.NewGuid(),
            RoomsJson = """[{"roomName":"Deluxe","pricePerNight":3000,"maxGuests":2,"floor":0}]""",
            RulesJson = """[{"from":"2026-11-06","to":"2026-11-12","lower":false,"percent":20,"label":"Diwali"}]""",
            BlocksJson = """[{"from":"2026-12-10","to":"2026-12-12"}]""",
        });
        Assert.Equal(new ListedRoom("Deluxe", 3000, 2, 0), Assert.Single(facts.Rooms));
        Assert.Equal("Diwali", Assert.Single(facts.Rules).Label);
        Assert.Single(facts.Blocks);
    }

    [Fact]
    public void Strip_clears_the_cache_rows_of_a_read_without_the_dated_pass()
    {
        var item = Stay(rules: [Rule(D(6, 10), D(6, 12), true, 15)], blocks: [new StayBlockRow { From = D(6, 20), To = D(6, 21) }]);
        StayPriceFacts.Strip([item]);
        Assert.Null(item.Rules);
        Assert.Null(item.Blocks);
    }

    private static List<StayRuleRow> VectorRules(JsonElement rules) => rules.EnumerateArray()
        .Where(r => !(r.TryGetProperty("paused", out var p) && p.GetBoolean()) && !(r.TryGetProperty("pausedByPlan", out var q) && q.GetBoolean()))
        .Select(r => Rule(DateOnly.Parse(r.GetProperty("from").GetString()!), DateOnly.Parse(r.GetProperty("to").GetString()!),
            r.GetProperty("direction").GetString() == "Lower", r.GetProperty("percent").GetInt32()))
        .ToList();
}
