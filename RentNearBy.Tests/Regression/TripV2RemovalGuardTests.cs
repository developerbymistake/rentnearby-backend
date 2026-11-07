using System.Reflection;
using System.Text.Json;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Regression;

// Guards from CONTRACT_V2 section 3: what trip flow v2 made dead stays gone, and the row wire shape stays as the apps read it.
public class TripV2RemovalGuardTests
{
    private static string[] Names(Type t)
        => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToArray();

    [Fact]
    public void TripOffer_has_no_needs_column_and_no_offer_shape_carries_needs()
    {
        Assert.DoesNotContain("NeedsEchoedJson", Names(typeof(TripOffer)));
        Assert.DoesNotContain("NeedsEchoed", Names(typeof(TripOfferDto)));
        Assert.DoesNotContain("NeedsEchoed", Names(typeof(SendTripOfferRequest)));
        Assert.DoesNotContain("Needs", Names(typeof(TripEnquiryContextDto)));
        Assert.DoesNotContain("Needs", Names(typeof(TripEnquiryContext)));
        Assert.Contains("Rooms", Names(typeof(TripEnquiryContextDto)));
        Assert.Contains("Rooms", Names(typeof(TripEnquiryContext)));
    }

    [Fact]
    public void Ask_again_is_gone()
    {
        Assert.Null(typeof(TripOffer).Assembly.GetType("RentNearBy.Core.DTOs.Requests.AskAgainRequest"));
        Assert.Null(typeof(TripEnquiryErrorTypes).GetField("AskAgainNothing"));
        Assert.Null(typeof(RentNearBy.Api.Handlers.TripEnquiryHandlers).GetMethod("AskAgain"));
    }

    [Fact]
    public void Trip_summary_card_keeps_only_the_row_progress_fields()
    {
        var names = Names(typeof(TripSummaryDto));
        Assert.Contains("Progress", names);
        Assert.Contains("BookingState", names);
        Assert.Contains("AgreedTotal", names);
        foreach (var gone in new[]
        {
            "TaxiDays", "StayDays", "StayName", "StaysConfirmed", "StaysTotal", "TaxisConfirmed", "TaxisTotal", "StaysOwn", "TaxisOwn",
            "TravelSegments", "TravelSettled", "Legs", "GettingThere", "ComingBack",
        })
            Assert.DoesNotContain(gone, names);
        Assert.Null(typeof(TripSummaryDto).Assembly.GetType("RentNearBy.Core.DTOs.Responses.TripSummaryLegStateDto"));
    }

    [Fact]
    public void Summary_record_has_no_own_fields_and_lists_arrangements()
    {
        foreach (var type in new[] { typeof(TripSummaryNightDto), typeof(TripSummaryTaxiDto) })
        {
            Assert.DoesNotContain("IsOwn", Names(type));
            Assert.DoesNotContain("OwnPhone", Names(type));
        }
        foreach (var gone in new[] { "How", "OwnName", "OwnPhone", "LeavesAt", "HubName" })
            Assert.DoesNotContain(gone, Names(typeof(TripSummaryTravelDto)));
        Assert.Contains("Arrangements", Names(typeof(TripSummaryRecordDto)));
        Assert.Equal(new[] { "Key", "Kind", "LegIndex", "Title", "State", "Detail" }, Names(typeof(TripSummaryArrangementDto)));
    }

    [Fact]
    public void Row_wire_shape_matches_the_contract()
    {
        var progress = new TripProgress
        {
            Total = 1, Done = 1, Percent = 100, Next = new TripNextRow { Kind = "taxi", Key = "taxi" },
            Rows = [new TripEasyRowDto { Key = "stay:1", Kind = "stay", LegIndex = 1, State = "asked", AskedCount = 1 }],
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(progress, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = doc.RootElement;
        Assert.Equal(
            new[] { "total", "done", "inProgress", "attention", "percent", "needsYou", "next", "lastEventAt", "rows" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "kind", "legIndex", "key" }, root.GetProperty("next").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(
            new[] { "key", "kind", "legIndex", "state", "needsYou", "askedCount", "nightsTotal", "nightsCovered" },
            root.GetProperty("rows")[0].EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Old_step_progress_types_are_gone()
    {
        var core = typeof(TripOffer).Assembly;
        foreach (var gone in new[]
        {
            "TripStepProgress", "TripStepInfo", "TripStepStates", "TripStepKinds", "TripNextStep", "TripBookingSummary", "TripBookingCounts",
            "TripStepDisplays", "TripStepsMerge", "TripPlanMerge", "TripOwnRules", "TripNeeds", "TripTaxiChoice", "TripStayChoice",
            "TripStayShortlistItem", "TripTaxiShortlistItem", "TripTaxiBooking", "TripTravelModes", "TripTravelHows", "TripTravelRoad", "TripDayStart",
        })
            Assert.Null(core.GetType("RentNearBy.Core.Models." + gone));
        foreach (var gone in new[] { "Display", "IsSorted", "NeedsNudge", "ClearDates" })
            Assert.Null(typeof(TripStepRules).GetMethod(gone));
        Assert.Null(typeof(TripLegRules).GetField("DriverInOtherCity"));
    }

    [Fact]
    public void Plan_v6_shape_has_no_day_level_stay_or_taxi_and_no_own_fields()
    {
        foreach (var gone in new[] { "Taxi", "Stay", "StayShortlist", "TaxiShortlist" }) Assert.DoesNotContain(gone, Names(typeof(TripDay)));
        foreach (var gone in new[] { "Rooms", "Needs", "PickupTime", "PickupPlace" }) Assert.DoesNotContain(gone, Names(typeof(TripSettings)));
        foreach (var gone in new[] { "Note", "How", "OwnName", "OwnPhone", "LeavesAt" }) Assert.DoesNotContain(gone, Names(typeof(TripStep)));
        foreach (var gone in new[] { "Mode", "How", "OwnName", "OwnPhone", "LeavesAt", "Note", "HubName" }) Assert.DoesNotContain(gone, Names(typeof(TripTravel)));
        Assert.Contains("StayMark", Names(typeof(TripCityLeg)));
        Assert.Contains("TravelMark", Names(typeof(TripCityLeg)));
        Assert.Contains("TaxiMark", Names(typeof(TripPlan)));
        Assert.Equal([6], TripPlan.SupportedVersions);
    }
}
