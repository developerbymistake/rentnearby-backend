using System.Text.Json;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be2;

namespace RentNearBy.Tests.Owner.Be2;

// CONTRACT_BACKEND card photos 8: oldPricePerNight / offerPricePerNight / offerPercent (lane BE2).
public class StayOfferFieldsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly StayDatedPricing Pricing = new();

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static StayRuleRow Rule(DateOnly from, DateOnly to, bool lower, int percent) => new() { From = from, To = to, Lower = lower, Percent = percent };

    private static StayNearbyDto Stay(int price, List<StayRuleRow> rules) => new()
    {
        Id = Guid.NewGuid(), Name = "Lakeview Homestay", MinPricePerNight = price, DistanceKm = 0.4,
        Rooms = [new StayNearbyRoomDto { RoomName = "Deluxe", MaxGuests = 2, PricePerNight = price }, new StayNearbyRoomDto { RoomName = "Family Room", MaxGuests = 4, PricePerNight = price * 2 }],
        Rules = rules,
    };

    [Fact]
    public void Undated_1500_at_15_gives_old_1500_offer_1280_percent_15()
        => Assert.Equal((1500, 1280, 15), StayOfferFields.Undated(1500, 15));

    [Fact]
    public void Undated_1800_at_15_gives_1530()
        => Assert.Equal((1800, 1530, 15), StayOfferFields.Undated(1800, 15));

    [Theory]
    [InlineData(null, 15)]
    [InlineData(1500, null)]
    [InlineData(0, 15)]
    [InlineData(1500, 0)]
    public void Undated_null_without_percent_or_price(int? price, int? percent)
        => Assert.Null(StayOfferFields.Undated(price, percent));

    [Fact]
    public void Undated_null_when_the_rounded_offer_is_not_below_the_base()
        => Assert.Null(StayOfferFields.Undated(100, 1));

    [Theory]
    [InlineData(1800, 1530, 15, true)]
    [InlineData(1800, 1800, 15, false)]
    [InlineData(1800, 2160, 15, false)]
    [InlineData(1800, 1530, null, false)]
    [InlineData(null, 1530, 15, false)]
    [InlineData(1800, null, 15, false)]
    public void Dated_fields_only_when_dates_price_below_base(int? basePrice, int? dates, int? percent, bool present)
    {
        var offer = StayOfferFields.Dated(basePrice, dates, percent);
        Assert.Equal(present, offer != null);
        if (present) Assert.Equal((basePrice!.Value, dates!.Value, percent!.Value), offer!.Value);
    }

    [Fact]
    public void Dated_higher_rule_gives_no_fields()
    {
        var item = Stay(3000, [Rule(D(11, 6), D(11, 12), false, 20)]);
        Pricing.Apply([item], D(11, 7), 2, null, null, false, D(11, 1));

        Assert.True(item.DatesPerNight > item.BasePerNight);
        Assert.Null(item.OldPricePerNight);
        Assert.Null(item.OfferPricePerNight);
        Assert.Null(item.OfferPercent);
    }

    [Fact]
    public void Dated_offer_equals_datesPerNight_and_old_equals_basePerNight_cheapest_room()
    {
        var item = Stay(1800, [Rule(D(12, 1), D(12, 25), true, 15)]);
        Pricing.Apply([item], D(12, 18), 3, null, null, false, D(12, 1));

        Assert.Equal(1530, item.DatesPerNight);
        Assert.Equal(item.BasePerNight, item.OldPricePerNight);
        Assert.Equal(item.DatesPerNight, item.OfferPricePerNight);
        Assert.Equal(15, item.OfferPercent);
        Assert.Equal(1800, item.MinPricePerNight);
    }

    [Fact]
    public void Dated_offer_equals_datesPerNight_and_old_equals_basePerNight_party_quote()
    {
        var item = Stay(3000, [Rule(D(11, 1), D(11, 30), true, 20)]);
        StayNearbyRooms.Apply([item], guests: 6, groundFloorOnly: false, roomsWanted: null, nights: 3);
        Assert.True(item.Fits);

        Pricing.Apply([item], D(11, 7), 3, 6, null, false, D(11, 1));

        Assert.NotNull(item.OfferPricePerNight);
        Assert.Equal(item.BasePerNight, item.OldPricePerNight);
        Assert.Equal(item.DatesPerNight, item.OfferPricePerNight);
        Assert.Equal(item.QuotePerNight, item.OfferPricePerNight);
        Assert.Equal(20, item.OfferPercent);
    }

    [Fact]
    public void Without_check_in_the_nearby_item_gets_no_offer_fields()
    {
        var item = Stay(1800, [Rule(D(12, 1), D(12, 25), true, 15)]);
        Pricing.Apply([item], null, 1, null, null, false, D(12, 1));

        var json = JsonSerializer.Serialize(item, Web);
        Assert.DoesNotContain("offerPricePerNight", json);
        Assert.DoesNotContain("oldPricePerNight", json);
        Assert.DoesNotContain("offerPercent", json);
    }

    [Fact]
    public void Fields_absent_from_json_when_null()
    {
        var dto = new StayBrowseDto { Id = Guid.NewGuid(), MinPricePerNight = 1800 };
        dto.SetOffer(StayOfferFields.Undated(1800, null));
        var json = JsonSerializer.Serialize(dto, Web);
        Assert.DoesNotContain("oldPricePerNight", json);
        Assert.DoesNotContain("offerPricePerNight", json);
        Assert.DoesNotContain("offerPercent", json);

        var feed = JsonSerializer.Serialize(new StayFeedItemDto { Id = Guid.NewGuid(), MinPricePerNight = 1800 }, Web);
        Assert.DoesNotContain("offerPercent", feed);
    }

    [Fact]
    public void MinPricePerNight_and_priceChip_unchanged()
    {
        var dto = new StayBrowseDto { Id = Guid.NewGuid(), MinPricePerNight = 1800, PriceChip = PriceChipDto.Of(15) };
        dto.SetOffer(StayOfferFields.Undated(dto.MinPricePerNight, 15));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, Web));
        var root = doc.RootElement;
        Assert.Equal(1800, root.GetProperty("minPricePerNight").GetInt32());
        Assert.Equal("15% off", root.GetProperty("priceChip").GetProperty("text").GetString());
        Assert.Equal(1800, root.GetProperty("oldPricePerNight").GetInt32());
        Assert.Equal(1530, root.GetProperty("offerPricePerNight").GetInt32());
        Assert.Equal(15, root.GetProperty("offerPercent").GetInt32());

        var feed = new StayFeedItemDto { Id = Guid.NewGuid(), MinPricePerNight = 1800 };
        feed.SetOffer(StayOfferFields.Undated(1800, 15));
        Assert.Contains("\"offerPricePerNight\":1530", JsonSerializer.Serialize(feed, Web));
    }

    [Fact]
    public void Call_sites_fill_the_fields_from_the_chip_percent()
    {
        var root = RentNearBy.Tests.Trips.CompletionCounterGuardTests.Root();
        string Src(params string[] parts) => File.ReadAllText(Path.Combine([root, .. parts]));
        Assert.Contains("SetOffer(StayOfferFields.Undated(r.Dto.MinPricePerNight, r.Chip))",
            Src("RentNearBy.Infrastructure", "Repositories", "StayListingRepository.cs"));
        Assert.Contains("SetOffer(StayOfferFields.Undated(x.Item.MinPricePerNight, x.Chip))",
            Src("RentNearBy.Infrastructure", "Repositories", "StayFeedHydrator.cs"));
        var dated = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be2", "StayDatedPricing.cs");
        Assert.Equal(2, dated.Split("SetOffer(StayOfferFields.Dated(item.BasePerNight, item.DatesPerNight, percent))").Length - 1);
    }
}
