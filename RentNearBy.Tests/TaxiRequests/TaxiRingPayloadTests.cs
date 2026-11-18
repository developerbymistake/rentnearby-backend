using System.Text.Json;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiRingPayloadTests
{
    private static readonly Guid OfferId = Guid.Parse("a1b2c3d4-0001-4a00-8000-000000000001");
    private static readonly Guid RequestId = Guid.Parse("3f9a6c2d-1b7e-4f0a-8c55-2e7d9b4a6101");

    private static JsonElement Sample(string name)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, name))).RootElement;

    private static Dictionary<string, string> DataOf(JsonElement sample)
        => sample.GetProperty("data").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

    private static TaxiRingSnapshot Snapshot() => new(
        new DateOnly(2026, 12, 18), new DateOnly(2026, 12, 21), "Kathgodam", ["Nainital", "Ranikhet"],
        true, true, true, 3, 4, 2);

    private static Dictionary<string, string> Ring(TaxiRingSnapshot? snapshot = null) => TaxiRingPayload.Build(
        OfferId, RequestId, new DateTime(2026, 12, 10, 9, 46, 7, DateTimeKind.Utc), new DateTime(2026, 12, 10, 9, 41, 8, DateTimeKind.Utc),
        0, "Qk9fY2xhdWRlX3NhbXBsZQ", snapshot ?? Snapshot());

    [Fact]
    public void Ring_data_equals_the_contract_sample()
    {
        var expected = DataOf(Sample("push_ring.json"));
        var actual = Ring();

        Assert.Equal(expected.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));
        Assert.Equal(expected.OrderBy(k => k.Key), actual.OrderBy(k => k.Key));
    }

    [Fact]
    public void Ring_has_no_personal_data()
    {
        var data = Ring();
        var banned = new[] { "name", "phone", "tripname", "travellername", "mobile", "email" };
        Assert.DoesNotContain(data.Keys, k => banned.Contains(k.ToLowerInvariant()));

        var text = string.Join('|', data.Values);
        Assert.DoesNotContain("+91", text);
        Assert.DoesNotContain("Aarav", text);
    }

    [Fact]
    public void Ring_end_equals_the_contract_sample()
    {
        var sample = Sample("push_ring_end.json");
        var parts = TaxiRingPayload.BuildEnd(OfferId, RequestId, "Filled");

        Assert.NotNull(parts);
        Assert.Equal(DataOf(sample).OrderBy(k => k.Key), parts!.Data.OrderBy(k => k.Key));
        Assert.Equal(sample.GetProperty("notification").GetProperty("title").GetString(), parts.Title);
        Assert.Equal(sample.GetProperty("notification").GetProperty("body").GetString(), parts.Body);
        Assert.Equal(sample.GetProperty("android").GetProperty("tag").GetString(), TaxiPushKinds.RingTag(OfferId));
        Assert.Equal(sample.GetProperty("android").GetProperty("channelId").GetString(), TaxiPushKinds.UpdatesChannelId);
    }

    [Theory]
    [InlineData("Filled", "Taken by another driver")]
    [InlineData("Booked", "Taken by another driver")]
    [InlineData("RequestCancelled", "The traveller cancelled the request")]
    [InlineData("DriverOffline", "You are offline")]
    public void Ring_end_body_follows_the_reason(string reason, string body)
        => Assert.Equal(body, TaxiRingPayload.BuildEnd(OfferId, RequestId, reason)!.Body);

    [Fact]
    public void Ring_end_has_no_push_for_a_reason_without_one()
        => Assert.Null(TaxiRingPayload.BuildEnd(OfferId, RequestId, "Expired"));

    [Fact]
    public void Route_line_adds_a_plus_count_after_three_cities()
    {
        Assert.Equal("Kathgodam to A, B, C +2", TaxiRingPayload.RouteLine("Kathgodam", ["A", "B", "C", "D", "E"]));
        Assert.Equal("Kathgodam to A, B, C", TaxiRingPayload.RouteLine("Kathgodam", ["A", "B", "C"]));
        Assert.Equal("A, B", TaxiRingPayload.RouteLine(null, ["A", "B"]));
        Assert.Equal("Kathgodam", TaxiRingPayload.RouteLine("Kathgodam", []));
    }

    [Fact]
    public void Dates_and_seat_lines_cover_edges()
    {
        Assert.Equal("1 day · 18 Dec", TaxiRingPayload.DatesLine(new DateOnly(2026, 12, 18), new DateOnly(2026, 12, 18)));
        Assert.Equal("2 days · 1 night · 31 Dec to 1 Jan", TaxiRingPayload.DatesLine(new DateOnly(2026, 12, 31), new DateOnly(2027, 1, 1)));
        Assert.Equal("6 seater · 1 traveller", TaxiRingPayload.SeatsLine(6, 1));
        Assert.Equal("7+ seater · 8 travellers", TaxiRingPayload.SeatsLine(7, 8));
    }

    [Fact]
    public void Sections_list_only_the_wanted_parts()
    {
        var data = Ring(Snapshot() with { Sightseeing = false, SightseeingDayCount = 0 });
        Assert.Equal("pickup,drop", data["sections"]);
        Assert.Equal("0", data["sightseeingDays"]);
    }
}
