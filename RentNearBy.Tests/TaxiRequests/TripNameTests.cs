using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation.TestHelper;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TripNameTests
{
    private static readonly SaveTripRequestValidator Validator = new();
    private static readonly string[] Nainital = ["Nainital"];
    private static readonly string[] Haridwar = ["Haridwar"];
    private static readonly string[] Two = ["Nainital", "Haridwar"];

    private static SaveTripRequest Save(string? name = null, string? title = null) => new()
    {
        Name = name,
        Title = title,
        Plan = new TripPlan
        {
            CityKey = 210000001,
            Legs = [new TripCityLeg { Index = 1, CityKey = 210000001, CityName = "Nainital", Latitude = 29.38, Longitude = 79.45 }],
            Days = [new TripDay { Day = 1, Spots = [] }],
        },
    };

    private static Trip Create(SaveTripRequest r) => TripHandlers.NewTrip(Guid.NewGuid(), r, r.Plan, DateTime.UtcNow);

    [Fact]
    public void Name_1_to_60_is_stored_as_title()
    {
        foreach (var length in new[] { 1, 60 })
        {
            var name = new string('a', length);
            var request = Save(name);
            Validator.TestValidate(request).ShouldNotHaveValidationErrorFor(x => x.Name);
            Assert.Equal(name, Create(request).Title);
        }
        Assert.Equal("Uttarakhand Hills Trip", Create(Save("  Uttarakhand   Hills Trip ")).Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_whitespace_name_is_rejected(string name) => AssertRejected(name);

    [Fact]
    public void Name_61_is_rejected() => AssertRejected(new string('a', 61));

    [Theory]
    [InlineData("Trip\nName")]
    [InlineData("Trip\tName")]
    [InlineData("Trip\u0000Name")]
    public void Control_characters_are_rejected(string name) => AssertRejected(name);

    [Theory]
    [InlineData("\u200B")]
    [InlineData("\u200B\u200B\u200B")]
    [InlineData("Trip\u200BName")]
    [InlineData("Trip \u202Eeman")]
    [InlineData("Trip\uFEFF")]
    [InlineData("Trip\u2028Name")]
    [InlineData("Trip\uE000")]
    [InlineData("Trip\u200D")]
    [InlineData("a\u200Db")]
    public void Invisible_characters_are_rejected(string name) => AssertRejected(name);

    [Theory]
    [InlineData("!!!")]
    [InlineData("- - -")]
    [InlineData("\U0001F3D4\uFE0F")]
    public void Name_without_a_letter_or_digit_is_rejected(string name) => AssertRejected(name);

    [Theory]
    [InlineData("https://spam.example")]
    [InlineData("Book at http://spam.example now")]
    [InlineData("www.spam.example trip")]
    [InlineData("Trip WWW.SPAM.EXAMPLE")]
    public void Name_with_a_link_is_rejected(string name) => AssertRejected(name);

    [Fact]
    public void Other_space_characters_become_one_space()
    {
        Assert.Equal("Hills Trip", TripNames.Clean("\u00A0Hills\u3000\u2003Trip "));
        Assert.Equal("Hills Trip", Create(Save("Hills\u00A0\u00A0Trip")).Title);
    }

    [Theory]
    [InlineData("Family \U0001F468\u200D\U0001F469\u200D\U0001F467 trip")]
    [InlineData("Manali \U0001F3D4\uFE0F 2026")]
    [InlineData("\u092E\u0928\u093E\u0932\u0940 \u092F\u093E\u0924\u094D\u0930\u093E")]
    [InlineData("Day 2")]
    [InlineData("7")]
    public void Real_names_with_emoji_or_other_scripts_are_kept(string name)
    {
        Validator.TestValidate(Save(name)).ShouldNotHaveValidationErrorFor(x => x.Name);
        Assert.Equal(name, TripNames.Clean(name));
    }

    private static void AssertRejected(string name)
    {
        var result = Validator.TestValidate(Save(name));
        var error = Assert.Single(result.Errors, e => e.PropertyName == nameof(SaveTripRequest.Name));
        Assert.Equal(TaxiRequestErrorTypes.TripNameInvalid, error.ErrorCode);
        Assert.Equal("Trip name must be 1 to 60 characters", error.ErrorMessage);
    }

    [Fact]
    public void Name_wins_over_title()
    {
        Assert.Equal("My name", Create(Save("My name", "Old title")).Title);
        Assert.Equal("My name", TripNames.Resolve("Old title", false, "My name", "Other title", Nainital, Nainital).Title);
    }

    [Fact]
    public void Name_sets_name_chosen()
    {
        Assert.True(Create(Save("My name")).NameChosen);
        Assert.True(TripNames.Resolve("Nainital trip", false, "My name", null, Nainital, Nainital).NameChosen);
    }

    [Fact]
    public void Chosen_name_is_not_retitled_when_cities_change()
    {
        Assert.Equal(new TripNameResult("My name", true), TripNames.Resolve("My name", true, null, null, Nainital, Two));
    }

    [Fact]
    public void Explicit_title_from_an_old_build_renames_a_chosen_trip()
    {
        Assert.Equal(new TripNameResult("Typed title", true), TripNames.Resolve("My name", true, null, "  Typed   title ", Nainital, Two));
        var longTitle = new string('t', 80);
        Assert.Equal(new TripNameResult(new string('t', 60), true), TripNames.Resolve("My name", true, null, longTitle, Nainital, Two));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u200B")]
    [InlineData("!!!")]
    [InlineData("www.spam.example")]
    public void Empty_or_invalid_title_keeps_the_chosen_name(string title)
        => Assert.Equal(new TripNameResult("My name", true), TripNames.Resolve("My name", true, null, title, Nainital, Two));

    [Fact]
    public void Name_still_wins_over_title_on_a_chosen_trip()
        => Assert.Equal(new TripNameResult("New name", true), TripNames.Resolve("My name", true, "New name", "Typed title", Nainital, Two));

    [Fact]
    public void Chosen_name_equal_to_the_default_text_is_not_retitled()
    {
        var text = TripTitles.Default(Nainital);
        var trip = Create(Save(text));
        Assert.True(trip.NameChosen);
        var result = TripNames.Resolve(trip.Title, trip.NameChosen, null, null, Nainital, Two);
        Assert.Equal(new TripNameResult(text, true), result);
    }

    [Fact]
    public void Legacy_default_title_still_follows_the_cities()
    {
        Assert.Equal(new TripNameResult(TripTitles.Default(Two), false),
            TripNames.Resolve(TripTitles.Default(Nainital), false, null, null, Nainital, Two));
        Assert.Equal("Typed", TripNames.Resolve("Typed", false, null, null, Nainital, Two).Title);
    }

    [Fact]
    public void Old_title_path_still_accepts_80()
    {
        var title = new string('t', 80);
        Validator.TestValidate(Save(title: title)).ShouldNotHaveValidationErrorFor(x => x.Title);
        Validator.TestValidate(Save(title: title + "t")).ShouldHaveValidationErrorFor(x => x.Title);
        Assert.Equal(new TripNameResult(title, false), TripNames.Resolve(null, false, null, title, [], Nainital));
        Assert.Equal(new TripNameResult(TripTitles.Default(Haridwar), false),
            TripNames.Resolve("Nainital trip", false, null, "  ", Nainital, Haridwar));
    }

    [Fact]
    public void Old_create_without_title_gets_the_default()
    {
        var request = Save();
        var trip = Create(request);
        Assert.Equal(TripTitles.Default(request.Plan.LegCities), trip.Title);
        Assert.False(trip.NameChosen);
    }

    [Fact]
    public void Trip_name_fields_equal_the_contract_sample()
    {
        var request = Save("Uttarakhand Hills Trip");
        var dto = TripHandlers.ToDto(Create(request), request.Plan);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var actual = JsonSerializer.SerializeToNode(new TripNameFieldsDto { Title = dto.Title, NameChosen = dto.NameChosen }, web);
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, "trip_name_fields.json")));
        Assert.True(JsonNode.DeepEquals(sample, actual));
        var full = JsonSerializer.SerializeToNode(dto, web)!.AsObject();
        Assert.True(full["nameChosen"]!.GetValue<bool>());
        Assert.Equal("Uttarakhand Hills Trip", full["title"]!.GetValue<string>());
    }
}
