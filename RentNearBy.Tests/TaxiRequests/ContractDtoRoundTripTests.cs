using System.Text.Json;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.DTOs.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// Each sample parses into its DTO with the API's JSON defaults (camelCase, nulls written) and writes back as an equal JSON tree.
public class ContractDtoRoundTripTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, Type> Map = new Dictionary<string, Type>
    {
        ["ack_request.json"] = typeof(TaxiRingAckDto),
        ["admin_taxi_bookings.json"] = typeof(AdminTaxiBookingListDto),
        ["admin_taxi_health.json"] = typeof(AdminTaxiHealthDto),
        ["admin_taxi_request_detail.json"] = typeof(AdminTaxiRequestDetailDto),
        ["admin_taxi_request_list.json"] = typeof(AdminTaxiRequestListDto),
        ["driver_bookings.json"] = typeof(DriverBookingsDto),
        ["driver_device_state.json"] = typeof(DriverDeviceStateDto),
        ["driver_offer_detail_ringing.json"] = typeof(DriverOfferDto),
        ["driver_offer_detail_selected.json"] = typeof(DriverOfferDto),
        ["driver_offer_list.json"] = typeof(DriverOfferListDto),
        ["driver_rating.json"] = typeof(DriverRatingDto),
        ["driver_readiness.json"] = typeof(DriverReadinessDto),
        ["error_all_drivers_busy.json"] = typeof(ApiResponse<object>),
        ["error_dates_already_booked.json"] = typeof(ApiResponse<DatesAlreadyBookedData>),
        ["error_legacy_taxi_active.json"] = typeof(ApiResponse<LegacyTaxiActiveData>),
        ["error_plan_locked.json"] = typeof(ApiResponse<PlanLockedData>),
        ["pickup_places.json"] = typeof(PickupPlacesDto),
        ["push_ring.json"] = typeof(TaxiPushMessageDto),
        ["push_ring_end.json"] = typeof(TaxiPushMessageDto),
        ["push_visible.json"] = typeof(TaxiPushMessageDto),
        ["signalr_taxi_request_changed.json"] = typeof(TaxiRequestChangedEvent),
        ["taxi_request_booked.json"] = typeof(TaxiRequestDto),
        ["taxi_request_busy.json"] = typeof(TaxiRequestDto),
        ["taxi_request_collecting.json"] = typeof(TaxiRequestDto),
        ["taxi_request_create.json"] = typeof(CreateTaxiRequestDto),
        ["taxi_request_finding.json"] = typeof(TaxiRequestDto),
        ["taxi_request_in_progress.json"] = typeof(TaxiRequestDto),
        ["taxi_request_no_reply.json"] = typeof(TaxiRequestDto),
        ["taxi_request_rate.json"] = typeof(TaxiRequestDto),
        ["taxi_request_selected.json"] = typeof(TaxiRequestDto),
        ["taxi_request_waiting.json"] = typeof(TaxiRequestDto),
        ["trip_invite.json"] = typeof(TripInviteDto),
        ["trip_name_fields.json"] = typeof(TripNameFieldsDto),
        ["trip_taxi_fields.json"] = typeof(TripTaxiFieldsDto),
    };

    public static IEnumerable<object[]> Samples() => Map.Keys.Select(k => new object[] { k });

    [Fact]
    public void Every_sample_has_exactly_one_case()
        => Assert.Equal(ContractSamplesGuardTests.Expected.Keys.OrderBy(k => k).ToArray(), Map.Keys.OrderBy(k => k).ToArray());

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_round_trips_as_an_equal_json_tree(string name)
    {
        var text = File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, name));
        var dto = JsonSerializer.Deserialize(text, Map[name], Web);
        Assert.NotNull(dto);
        var written = JsonSerializer.Serialize(dto, Map[name], Web);
        using var a = JsonDocument.Parse(text);
        using var b = JsonDocument.Parse(written);
        Assert.True(TreeEquals(a.RootElement, b.RootElement, "$", out var diff), diff);
    }

    [Fact]
    public void Ring_sample_values_land_in_the_typed_fields()
    {
        var text = File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, "driver_offer_detail_selected.json"));
        var o = JsonSerializer.Deserialize<DriverOfferDto>(text, Web)!;
        Assert.Equal("Selected", o.State);
        Assert.Equal(new DateOnly(2026, 12, 18), o.DateFrom);
        Assert.Equal(new DateTime(2026, 12, 10, 10, 1, 0, DateTimeKind.Utc), o.ConfirmBy);
        Assert.Equal(new DateOnly(2026, 12, 24), o.DatesConflict!.To);
        Assert.Equal(4, o.Trip!.Days.Count);
    }

    private static bool TreeEquals(JsonElement x, JsonElement y, string path, out string diff)
    {
        diff = "";
        if (x.ValueKind != y.ValueKind)
        {
            diff = $"{path}: {x.ValueKind} vs {y.ValueKind}";
            return false;
        }
        switch (x.ValueKind)
        {
            case JsonValueKind.Object:
                var xs = x.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var ys = y.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var missing = xs.Keys.Except(ys.Keys).Concat(ys.Keys.Except(xs.Keys)).ToArray();
                if (missing.Length > 0) { diff = $"{path}: property sets differ ({string.Join(",", missing)})"; return false; }
                foreach (var k in xs.Keys)
                    if (!TreeEquals(xs[k], ys[k], $"{path}.{k}", out diff)) return false;
                return true;
            case JsonValueKind.Array:
                var xa = x.EnumerateArray().ToArray();
                var ya = y.EnumerateArray().ToArray();
                if (xa.Length != ya.Length) { diff = $"{path}: length {xa.Length} vs {ya.Length}"; return false; }
                for (var i = 0; i < xa.Length; i++)
                    if (!TreeEquals(xa[i], ya[i], $"{path}[{i}]", out diff)) return false;
                return true;
            case JsonValueKind.Number:
                if (x.GetDecimal() == y.GetDecimal()) return true;
                diff = $"{path}: {x} vs {y}";
                return false;
            case JsonValueKind.String:
                if (x.GetString() == y.GetString()) return true;
                diff = $"{path}: '{x.GetString()}' vs '{y.GetString()}'";
                return false;
            default:
                return true;
        }
    }
}
