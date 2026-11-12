using System.Text.Json;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Trips;

// PlanJson v7: the optional guide (step, choices, overview) rides with the plan; v6 documents stay readable.
public class TripPlanV7Tests
{
    private static readonly SaveTripRequestValidator Validator = new();

    private static string PlanJson(int version, string? guide) => $$"""
    {
      "version": {{version}},
      "cityKey": 210000123, "cityName": "Nainital", "centreLatitude": 29.38, "centreLongitude": 79.45,
      "legs": [ { "index": 1, "cityKey": 210000123, "cityName": "Nainital", "latitude": 29.38, "longitude": 79.45 } ],
      "days": [ { "day": 1, "leg": 1, "spots": [] } ],
      "settings": { "dayLimitMinutes": 300 }
      {{(guide == null ? "" : ", \"guide\": " + guide)}}
    }
    """;

    private static SaveTripRequest Request(TripPlan plan) => new() { Title = "Trip", Status = "Draft", Plan = plan };

    [Fact]
    public void Version_is_seven_and_six_stays_supported()
    {
        Assert.Equal(7, TripPlan.CurrentVersion);
        Assert.Equal(new[] { 6, 7 }, TripPlan.SupportedVersions);
    }

    [Fact]
    public void V6_document_reads_without_a_guide()
    {
        var plan = TripPlanReader.Parse(PlanJson(6, null));
        plan.Normalize();
        Assert.Null(plan.Guide);
        Assert.Equal(6, plan.Version);
        Assert.True(Validator.Validate(Request(plan)).IsValid);
    }

    [Fact]
    public void V7_guide_round_trips_untouched()
    {
        var plan = TripPlanReader.Parse(PlanJson(7,
            """{ "step": "stay:2", "choices": { "gettingThere": "find", "stay:1": "skip", "taxi": "own" }, "overview": true }"""));
        plan.Normalize();
        var back = JsonSerializer.Deserialize<TripPlan>(JsonSerializer.Serialize(plan, TripJson.Options), TripJson.Options)!;
        Assert.Equal("stay:2", back.Guide!.Step);
        Assert.Equal("find", back.Guide.Choices!["gettingThere"]);
        Assert.Equal("skip", back.Guide.Choices["stay:1"]);
        Assert.Equal("own", back.Guide.Choices["taxi"]);
        Assert.True(back.Guide.Overview);
        Assert.True(Validator.Validate(Request(back)).IsValid);
    }

    [Fact]
    public void Default_guide_is_not_written()
    {
        var plan = TripPlanReader.Parse(PlanJson(7, """{ "overview": false }"""));
        plan.Normalize();
        Assert.Null(plan.Guide);
        Assert.DoesNotContain("guide", JsonSerializer.Serialize(plan, TripJson.Options));
    }

    [Fact]
    public void Unknown_step_and_choice_values_are_dropped_on_read()
    {
        var plan = TripPlanReader.Parse(PlanJson(7,
            """{ "step": "nowhere", "choices": { "stay:1": "find", "stay:0": "find", "taxi": "maybe", "bad": "own" } }"""));
        plan.Normalize();
        Assert.Null(plan.Guide!.Step);
        Assert.Equal(new[] { "stay:1" }, plan.Guide.Choices!.Keys);
    }

    [Theory]
    [InlineData("gettingThere", true)]
    [InlineData("comingBack", true)]
    [InlineData("taxi", true)]
    [InlineData("stay:1", true)]
    [InlineData("stay:12", true)]
    [InlineData("between:2", true)]
    [InlineData("stay:0", false)]
    [InlineData("stay:100", false)]
    [InlineData("between:1", false)]
    [InlineData("between:2x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Step_key_pattern(string? key, bool valid) => Assert.Equal(valid, TripStepKeys.IsValid(key));

    [Fact]
    public void Validator_rejects_a_bad_step_choice_value_and_too_many_choices()
    {
        var bad = TripPlanReader.Parse(PlanJson(7, null));
        bad.Normalize();
        bad.Guide = new TripGuide { Step = "stay:1", Choices = new() { ["stay:1"] = "maybe" } };
        Assert.Equal(SaveTripRequestValidator.GuideMessage, Validator.Validate(Request(bad)).Errors[0].ErrorMessage);

        bad.Guide = new TripGuide { Step = "stay:0" };
        Assert.False(Validator.Validate(Request(bad)).IsValid);

        bad.Guide = new TripGuide { Choices = Enumerable.Range(1, 31).ToDictionary(i => $"stay:{i}", _ => "skip") };
        Assert.False(Validator.Validate(Request(bad)).IsValid);

        bad.Guide.Choices = Enumerable.Range(1, 30).ToDictionary(i => $"stay:{i}", _ => "skip");
        Assert.True(Validator.Validate(Request(bad)).IsValid);
    }

    [Fact]
    public void City_info_carries_a_cover_and_the_snapshot_format_is_bumped()
    {
        Assert.Null(new TripCityInfo().CoverUrl);
        Assert.Equal(3, TripCityCatalogSnapshot.CurrentFormat);
    }
}
