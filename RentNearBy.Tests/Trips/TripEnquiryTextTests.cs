using System.Text.Json;

namespace RentNearBy.Tests.Trips;

public class TripEnquiryTextTests
{
    private static readonly DateOnly In = new(2026, 11, 1);

    private static TripEnquiryContext Ctx(object value) =>
        JsonSerializer.Deserialize<TripEnquiryContext>(JsonSerializer.Serialize(value, TripJson.Options), TripJson.Options)!;

    private static object Day(int day, string date, params string[] spots) => new { day, date, minutes = 335, spots };

    private static TripEnquiryContext Full() => Ctx(new
    {
        city = "Nainital",
        adults = 2,
        kids = 2,
        rooms = 1,
        needs = new[] { "no_stairs", "veg_food" },
        travellerName = "Rahul",
        days = new[] { Day(1, "2026-11-01", "Mall Road", "Naina Devi", "Naini Lake") },
    });

    [Fact]
    public void Contract_sample_one_night()
    {
        var text = TripEnquiryText.Build(Full(), In, new DateOnly(2026, 11, 2), false);
        Assert.Equal(
            "Hi, trip to Nainital, 01 Nov to 02 Nov, 2 adults, 2 kids, 1 room. We need no stairs and vegetarian food. Day 1 spots: Mall Road, Naina Devi, Naini Lake. Is a room free? Please share the best price.",
            text);
    }

    [Fact]
    public void One_adult_no_kids_no_needs_no_spots()
    {
        var ctx = Ctx(new { city = "Nainital", adults = 1, kids = 0, needs = Array.Empty<string>(), days = new[] { Day(1, "2026-11-01") } });
        var text = TripEnquiryText.Build(ctx, In, new DateOnly(2026, 11, 2), false);
        Assert.Contains("1 adult", text);
        Assert.DoesNotContain("1 adults", text);
        Assert.DoesNotContain("kids", text);
        Assert.DoesNotContain("We need", text);
        Assert.DoesNotContain("spots:", text);
        Assert.StartsWith("Hi, trip to Nainital, 01 Nov to 02 Nov", text);
        Assert.EndsWith("Is a room free? Please share the best price.", text);
    }

    [Fact]
    public void Two_rooms_are_plural()
    {
        var ctx = Ctx(new { city = "Nainital", adults = 4, kids = 0, rooms = 2, days = new[] { Day(1, "2026-11-01") } });
        var text = TripEnquiryText.Build(ctx, In, new DateOnly(2026, 11, 2), false);
        Assert.Contains("4 adults", text);
        Assert.Contains("2 rooms", text);
    }

    [Fact]
    public void Rooms_omitted_when_null()
    {
        var ctx = Ctx(new { city = "Nainital", adults = 2, days = new[] { Day(1, "2026-11-01") } });
        var text = TripEnquiryText.Build(ctx, In, new DateOnly(2026, 11, 2), false);
        Assert.DoesNotContain("room.", text);
        Assert.DoesNotContain("rooms", text);
    }

    [Fact]
    public void All_nights_adds_the_night_count_and_every_day()
    {
        var ctx = Ctx(new
        {
            city = "Nainital",
            adults = 2,
            days = new[] { Day(1, "2026-11-01", "Mall Road"), Day(2, "2026-11-02", "Naini Lake") },
        });
        var text = TripEnquiryText.Build(ctx, In, new DateOnly(2026, 11, 4), true);
        Assert.Contains("01 Nov to 04 Nov (3 nights)", text);
        Assert.Contains("Day 1 spots: Mall Road", text);
        Assert.Contains("Day 2 spots: Naini Lake", text);
    }

    [Fact]
    public void All_nights_with_one_night_has_no_night_count()
    {
        var text = TripEnquiryText.Build(Full(), In, new DateOnly(2026, 11, 2), true);
        Assert.DoesNotContain("night", text);
    }

    [Fact]
    public void Single_night_request_skips_the_night_count()
    {
        var text = TripEnquiryText.Build(Full(), In, new DateOnly(2026, 11, 4), false);
        Assert.DoesNotContain("(3 nights)", text);
    }

    [Fact]
    public void Needs_words()
    {
        var one = Ctx(new { city = "Nainital", adults = 2, needs = new[] { "kids_safe" }, days = new[] { Day(1, "2026-11-01") } });
        Assert.Contains("We need a safe room for kids.", TripEnquiryText.Build(one, In, new DateOnly(2026, 11, 2), false));

        var three = Ctx(new { city = "Nainital", adults = 2, needs = new[] { "no_stairs", "kids_safe", "veg_food" }, days = new[] { Day(1, "2026-11-01") } });
        Assert.Contains("We need no stairs, a safe room for kids and vegetarian food.", TripEnquiryText.Build(three, In, new DateOnly(2026, 11, 2), false));
    }

    [Fact]
    public void More_than_eight_spots_are_cut_with_a_count()
    {
        var spots = Enumerable.Range(1, 10).Select(i => $"S{i}").ToArray();
        var ctx = Ctx(new { city = "Nainital", adults = 2, days = new[] { Day(1, "2026-11-01", spots) } });
        var text = TripEnquiryText.Build(ctx, In, new DateOnly(2026, 11, 2), false);
        Assert.Contains("S8", text);
        Assert.DoesNotContain("S9", text);
        Assert.Contains("+2 more", text);
    }

    [Fact]
    public void Exactly_eight_spots_have_no_more_marker()
    {
        var spots = Enumerable.Range(1, 8).Select(i => $"S{i}").ToArray();
        var ctx = Ctx(new { city = "Nainital", adults = 2, days = new[] { Day(1, "2026-11-01", spots) } });
        Assert.DoesNotContain("more", TripEnquiryText.Build(ctx, In, new DateOnly(2026, 11, 2), false));
    }

    [Fact]
    public void Clean_removes_urls_control_characters_and_extra_spaces()
    {
        Assert.Equal("Hello world ok", TripTextSafety.Clean("  Hello\u0007  world https://evil.com/x  www.bad.com ok "));
        Assert.Equal("see now", TripTextSafety.Clean("see http://x.com/a now"));
        Assert.Equal("ab", TripTextSafety.Clean("a\u0000b"));
    }

    [Fact]
    public void Clean_cuts_to_80_characters()
    {
        Assert.Equal(80, TripTextSafety.Clean(new string('a', 200)).Length);
        Assert.Equal(80, TripTextSafety.Clean(new string('a', 80)).Length);
        Assert.Equal(79, TripTextSafety.Clean(new string('a', 79)).Length);
    }

    [Fact]
    public void Clean_handles_null_and_blank()
    {
        Assert.True(string.IsNullOrEmpty(TripTextSafety.Clean(null)));
        Assert.True(string.IsNullOrEmpty(TripTextSafety.Clean("   ")));
    }

    [Fact]
    public void Clean_keeps_plain_text_untouched()
    {
        Assert.Equal("Naina Devi Temple", TripTextSafety.Clean("Naina Devi Temple"));
    }

    [Fact]
    public void City_suffix_for_a_multi_city_trip()
    {
        Assert.Equal(" Part of a 4-day trip, 2 of 2 cities.", TripEnquiryText.CitySuffix(4, 2, 2));
        Assert.Equal("", TripEnquiryText.CitySuffix(4, 1, 1));
        Assert.Equal("", TripEnquiryText.CitySuffix(4, 1, 0));
    }

    [Fact]
    public void One_city_context_has_no_city_sentence_and_no_city_fields()
    {
        var ctx = Full();
        var text = TripEnquiryText.Build(ctx, In, new DateOnly(2026, 11, 2), false);
        Assert.DoesNotContain("Part of a", text);
        var json = JsonSerializer.Serialize(ctx, TripJson.Options);
        Assert.DoesNotContain("cityIndex", json);
        Assert.DoesNotContain("cityCount", json);
        Assert.DoesNotContain("tripDayCount", json);
    }

    [Fact]
    public void Multi_city_context_ends_with_the_city_sentence()
    {
        var ctx = Ctx(new
        {
            city = "Badrinath",
            adults = 2,
            days = new[] { Day(2, "2026-11-02", "Badrinath Temple") },
            cityIndex = 2,
            cityCount = 2,
            tripDayCount = 4,
        });

        var stay = TripEnquiryText.Build(ctx, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3), false);
        var lead = TripEnquiryText.BuildLead(ctx, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3));

        Assert.StartsWith("Hi, trip to Badrinath", stay);
        Assert.EndsWith("Please share the best price. Part of a 4-day trip, 2 of 2 cities.", stay);
        Assert.EndsWith(" Part of a 4-day trip, 2 of 2 cities.", lead);
        var back = Ctx(ctx);
        Assert.Equal(2, back.CityIndex);
        Assert.Equal(2, back.CityCount);
        Assert.Equal(4, back.TripDayCount);
    }
}
