using System.Text.Json;
using FluentValidation.Results;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Validation;

// The exact payload shapes of the owner's phone test (2026-11-07), run through the real validators and pure rules.
public class OwnerPayloadTests
{
    private static readonly SaveJourneyRequestValidator SaveJourney = new();
    private static readonly AskDriversRequestValidator AskDrivers = new();
    private static readonly SaveTripRequestValidator SaveTrip = new();

    private static DateOnly Soon => JourneyRules.TodayIst(DateTime.UtcNow).AddDays(10);

    private static readonly string Kamalwaganja = TaxiWaysKeys.VirtualKey(29.2189, 79.5130, false);
    private static readonly string Kathgodam = TaxiWaysKeys.PlaceKeyAtlas(210000123);
    private static readonly string Nainital = TaxiWaysKeys.PlaceKeyAtlas(210000456);
    private static readonly string Almora = TaxiWaysKeys.PlaceKeyAtlas(210000789);

    private static SaveJourneyLegRequest Leg(int index, string from, string to, string fromName = "", string toName = "") => new()
    {
        LegIndex = index, LegKey = TaxiWaysKeys.LegKey(from, to), FromName = fromName, ToName = toName,
        FromLat = 29.2, FromLng = 79.5, ToLat = 29.4, ToLng = 79.4,
        DriverTaxiId = Guid.NewGuid(), ListedFare = 0, CoverTaxiIds = new(),
    };

    private static SaveJourneyRequest Kamal(Guid? tripId = null, int? legIndex = null) => new()
    {
        FromName = "", FromLat = 29.2189, FromLng = 79.5130, ToName = "Nainital", ToLat = 29.38, ToLng = 79.46,
        Date = Soon, People = 4,
        PlanKey = TaxiWaysKeys.PlanKey([Kamalwaganja, Kathgodam, Nainital]),
        Legs = [Leg(1, Kamalwaganja, Kathgodam, "", "Kathgodam Railway Station"), Leg(2, Kathgodam, Nainital, "Kathgodam Railway Station", "Nainital")],
        TripId = tripId, LegIndex = legIndex,
    };

    private static SaveJourneyRequest Single(Guid? b1, Guid? b2) => new()
    {
        FromName = "Nainital", FromLat = 29.38, FromLng = 79.46, ToName = "Almora", ToLat = 29.59, ToLng = 79.65,
        Date = Soon, People = 3,
        PlanKey = TaxiWaysKeys.PlanKey([Nainital, Almora]),
        Legs = [new SaveJourneyLegRequest
        {
            LegIndex = 1, LegKey = TaxiWaysKeys.LegKey(Nainital, Almora), FromName = "Nainital", ToName = "Almora",
            FromLat = 29.38, FromLng = 79.46, ToLat = 29.59, ToLng = 79.65,
            DriverTaxiId = Guid.NewGuid(), ListedFare = 3200, Backup1TaxiId = b1, Backup2TaxiId = b2,
        }],
    };

    [Fact]
    public void Kamalwaganja_TwoLegs_BlankNames_NoTrip_Valid()
        => ValidationAssert.Valid(SaveJourney.Validate(Kamal()));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Kamalwaganja_TwoLegs_WithTrip_Valid(int legIndex)
        => ValidationAssert.Valid(SaveJourney.Validate(Kamal(Guid.NewGuid(), legIndex)));

    [Fact]
    public void Kamalwaganja_TripWithoutLegIndex_TypedMessage()
        => ValidationAssert.HasMessage(SaveJourney.Validate(Kamal(Guid.NewGuid(), null)), SaveJourneyRequestValidator.PickCityMessage);

    [Fact]
    public void Kamalwaganja_VirtualKey_IsAValidPlanAndLegKey()
    {
        Assert.StartsWith("v:", Kamalwaganja);
        Assert.True(TaxiWaysKeys.IsValidKey(Kamal().PlanKey));
        Assert.True(TaxiWaysKeys.TryParsePlanKey(Kamal().PlanKey, out var keys));
        Assert.Equal(new[] { Kamalwaganja, Kathgodam, Nainital }, keys);
        Assert.True(TaxiWaysKeys.IsValidKey(Kamal().Legs[0].LegKey));
    }

    [Fact]
    public void Kamalwaganja_LongNamesEverywhere_TypedError()
    {
        var request = Kamal();
        var long81 = new string('x', JourneyRules.NameMax + 1);
        request.FromName = long81;
        request.Legs[0].FromName = long81;
        request.Legs[1].ToName = long81;
        var result = SaveJourney.Validate(request);
        Assert.False(result.IsValid);
        ValidationAssert.HasMessage(result, "Place name is up to 80 characters");
    }

    [Fact]
    public void Kamalwaganja_NamesAtLimit_Valid()
    {
        var request = Kamal();
        var max = new string('x', JourneyRules.NameMax);
        request.FromName = max;
        request.Legs[0].FromName = max;
        request.Legs[0].MeetingPlace = max;
        ValidationAssert.Valid(SaveJourney.Validate(request));
    }

    [Fact]
    public void Single_Nainital_Almora_NoBackups_Valid()
        => ValidationAssert.Valid(SaveJourney.Validate(Single(null, null)));

    [Fact]
    public void Single_Backups_DuplicatesSameAsPrimaryAndEmptyGuid_NeverThrow()
    {
        var dup = Guid.NewGuid();
        var request = Single(dup, dup);
        request.Legs[0].Backup1TaxiId = request.Legs[0].DriverTaxiId;
        var cases = new[]
        {
            request,
            Single(dup, dup),
            Single(Guid.Empty, Guid.Empty),
            Single(Guid.Empty, null),
            Single(null, Guid.NewGuid()),
        };
        foreach (var c in cases)
        {
            Assert.Null(Record.Exception(() => SaveJourney.Validate(c)));
            ValidationAssert.Valid(SaveJourney.Validate(c));
        }
    }

    [Fact]
    public void Single_EmptyDriver_TypedError()
    {
        var request = Single(null, null);
        request.Legs[0].DriverTaxiId = Guid.Empty;
        ValidationAssert.HasMessage(SaveJourney.Validate(request), "Driver is required");
    }

    [Fact]
    public void Single_ZeroPeopleOrTooMany_TypedError()
    {
        foreach (var people in new[] { 0, 13, 50 })
        {
            var request = Single(null, null);
            request.People = people;
            ValidationAssert.HasMessage(SaveJourney.Validate(request), "People must be 1 to 12");
        }
    }

    [Fact]
    public void Single_PastAndFarDates_TypedError()
    {
        var today = JourneyRules.TodayIst(DateTime.UtcNow);
        foreach (var date in new[] { today.AddDays(-1), today.AddDays(400), default })
        {
            var request = Single(null, null);
            request.Date = date;
            ValidationAssert.HasMessage(SaveJourney.Validate(request), "Pick a date within the next year");
        }
    }

    [Fact]
    public void SaveJourney_Malformed_NeverThrows()
    {
        var cases = new List<SaveJourneyRequest>
        {
            new(),
            new() { Legs = null! },
            new() { PlanKey = null!, FromName = null!, ToName = null!, Legs = null! },
            Single(null, null),
        };
        var nullLeg = Single(null, null);
        nullLeg.Legs.Add(null!);
        cases.Add(nullLeg);
        var nanLat = Single(null, null);
        nanLat.FromLat = double.NaN;
        nanLat.Legs[0].ToLng = double.PositiveInfinity;
        cases.Add(nanLat);
        var nullCover = Single(null, null);
        nullCover.Legs[0].CoverTaxiIds = null!;
        cases.Add(nullCover);
        var badLegs = Single(null, null);
        badLegs.Legs = [Leg(2, Nainital, Almora), Leg(2, Nainital, Almora), Leg(3, Nainital, Almora), Leg(1, Nainital, Almora)];
        cases.Add(badLegs);

        foreach (var c in cases)
            Assert.Null(Record.Exception(() => SaveJourney.Validate(c)));
        Assert.False(SaveJourney.Validate(nullLeg).IsValid);
        Assert.False(SaveJourney.Validate(nanLat).IsValid);
        Assert.False(SaveJourney.Validate(badLegs).IsValid);
        Assert.False(SaveJourney.Validate(new SaveJourneyRequest()).IsValid);
    }

    // ---- AskDriversRequest ----

    private static AskDriversRequest Ask(string fromName = "", string toName = "", params Guid[][] taxisPerLeg) => new()
    {
        Date = Soon, People = 4, JourneyId = Guid.NewGuid(), FromName = fromName, ToName = toName,
        Legs = taxisPerLeg.Select((ids, i) => new AskLegRequest
        {
            LegIndex = i + 1, LegKey = TaxiWaysKeys.LegKey(Kamalwaganja, Kathgodam), FromName = "", ToName = "",
            TaxiIds = ids.ToList(), CarOf = 1,
        }).ToList(),
    };

    [Fact]
    public void Ask_BlankNames_TwoLegs_Valid()
        => ValidationAssert.Valid(AskDrivers.Validate(Ask("", "", [Guid.NewGuid()], [Guid.NewGuid(), Guid.NewGuid()])));

    [Fact]
    public void Ask_NullNames_Valid()
    {
        var request = Ask("", "", [Guid.NewGuid()]);
        request.FromName = null!;
        request.ToName = null!;
        request.Legs[0].FromName = null!;
        request.Legs[0].ToName = null!;
        ValidationAssert.Valid(AskDrivers.Validate(request));
    }

    [Fact]
    public void Ask_LongName_TypedError()
    {
        var request = Ask(new string('a', 81), "", [Guid.NewGuid()]);
        ValidationAssert.HasMessage(AskDrivers.Validate(request), "Place name is up to 80 characters");
    }

    [Fact]
    public void Ask_DuplicateAndEmptyDrivers_TypedError()
    {
        var one = Guid.NewGuid();
        ValidationAssert.HasMessage(AskDrivers.Validate(Ask("", "", [one, one])), "Invalid drivers");
        ValidationAssert.HasMessage(AskDrivers.Validate(Ask("", "", [Guid.Empty])), "Invalid drivers");
    }

    [Fact]
    public void Ask_NoDriversOrTooMany_TypedError()
    {
        ValidationAssert.HasMessage(AskDrivers.Validate(Ask("", "", Array.Empty<Guid>())), "Ask 1 to 3 drivers per car");
        ValidationAssert.HasMessage(
            AskDrivers.Validate(Ask("", "", [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()])),
            "Ask 1 to 3 drivers per car");
        ValidationAssert.HasMessage(
            AskDrivers.Validate(Ask("", "",
                [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()], [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()], [Guid.NewGuid()])),
            "Ask up to 6 drivers at once");
    }

    [Fact]
    public void Ask_Malformed_NeverThrows()
    {
        var cases = new List<AskDriversRequest> { new(), new() { Legs = null! } };
        var nullLeg = Ask("", "", [Guid.NewGuid()]);
        nullLeg.Legs.Add(null!);
        cases.Add(nullLeg);
        var nullIds = Ask("", "", [Guid.NewGuid()]);
        nullIds.Legs[0].TaxiIds = null!;
        cases.Add(nullIds);
        var dupLegs = Ask("", "", [Guid.NewGuid()], [Guid.NewGuid()]);
        dupLegs.Legs[1].LegIndex = 1;
        cases.Add(dupLegs);

        foreach (var c in cases)
            Assert.Null(Record.Exception(() => AskDrivers.Validate(c)));
        Assert.False(AskDrivers.Validate(new AskDriversRequest()).IsValid);
        Assert.False(AskDrivers.Validate(nullLeg).IsValid);
        Assert.False(AskDrivers.Validate(nullIds).IsValid);
        Assert.False(AskDrivers.Validate(dupLegs).IsValid);
    }

    // ---- SaveTripRequest: own everything, blank ----

    private const string Spot = "{\"id\":\"osm:N1\",\"name\":\"Mall Road\",\"latitude\":29.39,\"longitude\":79.45,\"category\":\"attraction\",\"visitMinutes\":60}";

    private static SaveTripRequest Trip(string settings, string steps, string days, string? legs = null)
    {
        var json = "{\"status\":\"Planned\",\"plan\":{\"version\":5,\"cityKey\":502141,\"cityName\":\"Nainital\",\"settings\":" + settings
                   + (steps.Length > 0 ? ",\"steps\":" + steps : "") + (legs != null ? ",\"legs\":" + legs : "")
                   + ",\"days\":" + days + "}}";
        return JsonSerializer.Deserialize<SaveTripRequest>(json, TripJson.Options)!;
    }

    private static ValidationResult Validate(SaveTripRequest request)
    {
        ValidationResult? result = null;
        Assert.Null(Record.Exception(() => result = SaveTrip.Validate(request)));
        return result!;
    }

    [Fact]
    public void Trip_OwnEverythingBlank_Valid_AndFallbacksApplied()
    {
        var request = Trip(
            "{\"dayLimitMinutes\":450,\"adults\":2,\"pickupPlace\":\"  \"}",
            "{\"startFrom\":{\"name\":\"\",\"lat\":29.2,\"lon\":79.5},\"goingHomeTo\":{\"name\":\"   \",\"lat\":29.2,\"lon\":79.5},"
            + "\"gettingThere\":{\"status\":\"own\",\"how\":\"\",\"ownName\":\"\",\"ownPhone\":\"\",\"note\":\"\"},"
            + "\"comingBack\":{\"status\":\"own\"}}",
            "[{\"day\":1,\"spots\":[" + Spot + "],\"taxi\":{\"isOwn\":true,\"name\":\"\",\"ownPhone\":\"\",\"note\":\"\"},"
            + "\"stay\":{\"isOwn\":true,\"name\":\"  \",\"ownPhone\":\"98\",\"note\":\"\"}}]");

        ValidationAssert.Valid(Validate(request));

        var steps = request.Plan.Steps!;
        Assert.Equal("My location", steps.StartFrom!.Name);
        Assert.Equal("Drop point", steps.GoingHomeTo!.Name);
        Assert.Null(steps.GettingThere!.How);
        Assert.Null(steps.GettingThere.OwnName);
        Assert.Null(steps.GettingThere.OwnPhone);
        Assert.Null(request.Plan.Settings.PickupPlace);
        Assert.Equal(string.Empty, request.Plan.Days[0].Taxi!.Name);
        Assert.Null(request.Plan.Days[0].Stay!.OwnPhone);
    }

    [Theory]
    [InlineData("flight")]
    [InlineData("car")]
    [InlineData("bus")]
    [InlineData("train")]
    [InlineData("other")]
    public void Trip_OwnTravelModes_Valid_AndKept(string how)
    {
        var request = Trip("{\"dayLimitMinutes\":450}",
            "{\"gettingThere\":{\"status\":\"own\",\"how\":\"" + how + "\",\"ownName\":\"IndiGo 6E-123\",\"ownPhone\":\"+91 98765 43210\"}}",
            "[{\"day\":1,\"spots\":[" + Spot + "]}]");

        ValidationAssert.Valid(Validate(request));
        Assert.Equal(how, request.Plan.Steps!.GettingThere!.How);
        Assert.Equal("9876543210", request.Plan.Steps.GettingThere.OwnPhone);
    }

    [Fact]
    public void Trip_UnknownHowAndBadPhoneAndTime_DroppedNotRefused()
    {
        var request = Trip("{\"dayLimitMinutes\":450}",
            "{\"comingBack\":{\"status\":\"own\",\"how\":\"teleport\",\"ownPhone\":\"12345\",\"leavesAt\":\"99:99\"}}",
            "[{\"day\":1,\"spots\":[" + Spot + "]}]");

        ValidationAssert.Valid(Validate(request));
        var back = request.Plan.Steps!.ComingBack!;
        Assert.Null(back.How);
        Assert.Null(back.OwnPhone);
        Assert.Null(back.LeavesAt);
    }

    [Fact]
    public void Trip_NonOwnStepWithOwnFields_FieldsClearedNotRefused()
    {
        var request = Trip("{\"dayLimitMinutes\":450}",
            "{\"gettingThere\":{\"status\":\"notStarted\",\"how\":\"bus\",\"ownName\":\"x\",\"ownPhone\":\"abc\"}}",
            "[{\"day\":1,\"spots\":[" + Spot + "]}]");

        ValidationAssert.Valid(Validate(request));
        Assert.Null(request.Plan.Steps!.GettingThere!.OwnPhone);
    }

    [Fact]
    public void Trip_LongOwnNamesAndNotes_CutNotRefused()
    {
        var name = new string('n', 500);
        var note = new string('t', 900);
        var request = Trip(
            "{\"dayLimitMinutes\":450,\"pickupPlace\":\"" + new string('p', 200) + "\"}",
            "{\"startFrom\":{\"name\":\"" + name + "\",\"lat\":29.2,\"lon\":79.5},"
            + "\"gettingThere\":{\"status\":\"own\",\"ownName\":\"" + name + "\",\"note\":\"" + note + "\"}}",
            "[{\"day\":1,\"spots\":[" + Spot + "],\"taxi\":{\"isOwn\":true,\"name\":\"" + name + "\",\"vehicleTypeName\":\"" + name
            + "\",\"note\":\"" + note + "\"},\"stay\":{\"isOwn\":true,\"name\":\"" + name + "\",\"note\":\"" + note + "\"}}]");

        ValidationAssert.Valid(Validate(request));
        var day = request.Plan.Days[0];
        Assert.Equal(80, day.Taxi!.Name.Length);
        Assert.Equal(80, day.Stay!.Name.Length);
        Assert.Equal(300, day.Stay.Note!.Length);
        Assert.Equal(80, request.Plan.Steps!.StartFrom!.Name.Length);
        Assert.Equal(80, request.Plan.Settings.PickupPlace!.Length);
        Assert.Equal(300, request.Plan.Steps.GettingThere!.Note!.Length);
    }

    [Fact]
    public void Trip_NonOwnStayLongNameAndShortlistNames_CutNotRefused()
    {
        var name = new string('s', 400);
        var id = Guid.NewGuid();
        var request = Trip("{\"dayLimitMinutes\":450}", "",
            "[{\"day\":1,\"spots\":[" + Spot + "],\"stay\":{\"stayId\":\"" + id + "\",\"name\":\"" + name + "\"},"
            + "\"stayShortlist\":[{\"stayId\":\"" + id + "\",\"name\":\"" + name + "\"}]}]");

        ValidationAssert.Valid(Validate(request));
        Assert.Equal(120, request.Plan.Days[0].Stay!.Name.Length);
        Assert.Equal(120, request.Plan.Days[0].StayShortlist![0].Name.Length);
    }

    [Fact]
    public void Trip_TwoCities_OwnArrivalBlank_Flight_BadPhone_Valid()
    {
        var request = Trip("{\"dayLimitMinutes\":450}", "",
            "[{\"day\":1,\"leg\":1,\"spots\":[" + Spot + "]},{\"day\":2,\"leg\":2,\"spots\":[" + Spot.Replace("N1", "N2") + "],"
            + "\"stay\":{\"isOwn\":true,\"name\":\"\"}}]",
            "[{\"index\":1,\"cityKey\":502141,\"cityName\":\"Nainital\",\"latitude\":29.38,\"longitude\":79.46},"
            + "{\"index\":2,\"cityKey\":502142,\"cityName\":\"Almora\",\"latitude\":29.59,\"longitude\":79.65,"
            + "\"arrival\":{\"mode\":\"own\",\"fromName\":\"\",\"how\":\"flight\",\"ownName\":\"\",\"ownPhone\":\"12\",\"note\":\"\"}}]");

        ValidationAssert.Valid(Validate(request));
        var arrival = request.Plan.Legs![1].Arrival!;
        Assert.Equal("flight", arrival.How);
        Assert.Null(arrival.OwnPhone);
        Assert.Null(arrival.OwnName);
    }

    [Fact]
    public void Trip_EmptyPlanBody_TypedErrorsNoThrow()
    {
        var request = new SaveTripRequest { Plan = null! };
        var result = Validate(request);
        Assert.False(result.IsValid);
        ValidationAssert.HasMessage(result, "Plan is required");
    }

    [Fact]
    public void Trip_NullSpotDayAndTitleTooLong_TypedErrorsNoThrow()
    {
        var request = Trip("{\"dayLimitMinutes\":450}", "", "[{\"day\":1,\"spots\":[null]}]");
        request.Title = new string('t', 81);
        var result = Validate(request);
        Assert.False(result.IsValid);
        ValidationAssert.HasMessage(result, "Title can be up to 80 characters");
    }

    // ---- TaxiWaysSearchEngine anchors and name overlay (pure) ----

    private static TaxiWaysNetworkSnapshot Snapshot() => TaxiWaysFixture.Build(
        TaxiWaysFixture.Row("Haridwar", TaxiWaysFixture.Haridwar, "Rishikesh", TaxiWaysFixture.Rishikesh, 900));

    [Fact]
    public void AnchorFromKey_VirtualStart_NeverBlankName()
    {
        var key = TaxiWaysKeys.VirtualKey(29.2189, 79.5130, false);
        var anchor = TaxiWaysSearchEngine.AnchorFromKey(Snapshot(), key, TaxiWaysFixture.Options);

        Assert.NotNull(anchor);
        Assert.Equal(key, anchor!.Key);
        Assert.Equal("Pickup point", anchor.Name);
        Assert.False(anchor.MyLocation);
        Assert.Equal(29.219, anchor.Lat, 3);
    }

    [Fact]
    public void AnchorFromKey_VirtualEnd_IsDropPoint()
    {
        var key = TaxiWaysKeys.VirtualKey(29.38, 79.46, false);
        var anchor = TaxiWaysSearchEngine.AnchorFromKey(Snapshot(), key, TaxiWaysFixture.Options, true);

        Assert.Equal("Drop point", anchor!.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnchorFromKey_MyLocation_IsMyLocationAtEitherEnd(bool isEnd)
    {
        var key = TaxiWaysKeys.VirtualKey(29.2189, 79.5130, true);
        var anchor = TaxiWaysSearchEngine.AnchorFromKey(Snapshot(), key, TaxiWaysFixture.Options, isEnd);

        Assert.Equal("My location", anchor!.Name);
        Assert.True(anchor.MyLocation);
    }

    [Theory]
    [InlineData("a999999999")]
    [InlineData("v:abc:def")]
    [InlineData("v:95:10")]
    [InlineData("v:1")]
    [InlineData("zzz")]
    [InlineData("")]
    public void AnchorFromKey_UnknownOrMalformedKey_NullNoThrow(string key)
    {
        TaxiWaysAnchor? anchor = null;
        Assert.Null(Record.Exception(() => anchor = TaxiWaysSearchEngine.AnchorFromKey(Snapshot(), key, TaxiWaysFixture.Options)));
        Assert.Null(anchor);
    }

    private static WayPlanDto Plan(string startKey, string endKey) => new()
    {
        PlanKey = TaxiWaysKeys.PlanKey([startKey, Kathgodam, endKey]), Cars = 2, Total = 4000,
        Legs =
        [
            new WayLegDto { LegKey = TaxiWaysKeys.LegKey(startKey, Kathgodam), Index = 1, From = new WayPlaceDto { Key = startKey, Name = "Pickup point" }, To = new WayPlaceDto { Key = Kathgodam, Name = "Kathgodam Railway Station" } },
            new WayLegDto { LegKey = TaxiWaysKeys.LegKey(Kathgodam, endKey), Index = 2, From = new WayPlaceDto { Key = Kathgodam, Name = "Kathgodam Railway Station" }, To = new WayPlaceDto { Key = endKey, Name = "Drop point" } },
        ],
    };

    [Fact]
    public void NameOverlay_TypedNames_ApplyToVirtualEndsOnly_AndNeverTouchTheCachedCopy()
    {
        var cached = new List<WayPlanDto> { Plan(Kamalwaganja, TaxiWaysKeys.VirtualKey(29.38, 79.46, false)) };
        var endKey = cached[0].Legs[1].To.Key;

        var shown = TaxiWaysNameOverlay.Apply(cached, Kamalwaganja, "Kamalwaganja", endKey, "Nainital Mall Road");

        Assert.Equal("Kamalwaganja", shown[0].Legs[0].From.Name);
        Assert.Equal("Nainital Mall Road", shown[0].Legs[1].To.Name);
        Assert.Equal("Kathgodam Railway Station", shown[0].Legs[0].To.Name);
        Assert.Equal("Pickup point", cached[0].Legs[0].From.Name);
        Assert.Equal("Drop point", cached[0].Legs[1].To.Name);
    }

    [Fact]
    public void NameOverlay_NoNames_ReturnsSameList_AndRealPlaceKeepsItsName()
    {
        var cached = new List<WayPlanDto> { Plan(Nainital, Almora) };

        Assert.Same(cached, TaxiWaysNameOverlay.Apply(cached, Nainital, null, Almora, null));

        var shown = TaxiWaysNameOverlay.Apply(cached, Nainital, "Typed", Almora, "Typed too");
        Assert.Equal("Pickup point", shown[0].Legs[0].From.Name);
        Assert.Equal("Drop point", shown[0].Legs[1].To.Name);
    }

    [Fact]
    public void NameOverlay_EmptyPlansAndLegless_NoThrow()
    {
        Assert.Empty(TaxiWaysNameOverlay.Apply(new List<WayPlanDto>(), Kamalwaganja, "a", Nainital, "b"));
        var legless = new WayPlanDto();
        Assert.Null(Record.Exception(() => TaxiWaysNameOverlay.ApplyTo(legless, Kamalwaganja, "a", Nainital, "b")));
    }

    [Fact]
    public void Labels_TypedNameIsCutToMax_BlankFallsBackByKey()
    {
        Assert.Equal(JourneyRules.NameMax, TaxiWaysLabels.Of(new string('q', 300), Kamalwaganja, true, false).Length);
        Assert.Equal("Pickup point", TaxiWaysLabels.Of("", Kamalwaganja, true, false));
        Assert.Equal("Drop point", TaxiWaysLabels.Of(null, TaxiWaysKeys.VirtualKey(1, 2, false), false, true));
        Assert.Equal("My location", TaxiWaysLabels.Of("  ", TaxiWaysKeys.VirtualKey(1, 2, true), true, false));
        Assert.Equal("Stop", TaxiWaysLabels.Of(null, Kathgodam, false, false));
    }
}
