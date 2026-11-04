using System.Text.Json;
using FluentValidation;
using FluentValidation.TestHelper;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Validation;

public class TripLifecycleValidatorsTests
{
    private static T Req<T>(Dictionary<string, object?> body, params (string Key, object? Value)[] overrides)
    {
        var copy = new Dictionary<string, object?>(body);
        foreach (var (k, v) in overrides) copy[k] = v;
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(copy, TripJson.Options), TripJson.Options)!;
    }

    private static void Fails<T>(IValidator<T> validator, T request, string property)
    {
        var result = validator.TestValidate(request);
        Assert.Contains(result.Errors, e => e.PropertyName == property);
    }

    private static void Passes<T>(IValidator<T> validator, T request)
    {
        var result = validator.TestValidate(request);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}")));
    }

    private static readonly TripProblemRequestValidator Problem = new();
    private static readonly ReportTravellerRequestValidator ReportTraveller = new();

    private static Dictionary<string, object?> ProblemBody() => new()
    {
        ["day"] = 1, ["kind"] = "DriverNoShow", ["enquiryId"] = Guid.NewGuid(), ["note"] = null, ["latitude"] = null, ["longitude"] = null,
    };

    [Fact]
    public void Problem_valid_passes() => Passes(Problem, Req<TripProblemRequest>(ProblemBody()));

    [Theory]
    [InlineData("DriverNoShow")]
    [InlineData("HostNoRoom")]
    [InlineData("RoadClosed")]
    [InlineData("Other")]
    public void Problem_known_kinds_pass(string kind) => Passes(Problem, Req<TripProblemRequest>(ProblemBody(), ("kind", kind)));

    [Theory]
    [InlineData("")]
    [InlineData("driverNoShow")]
    [InlineData("Fire")]
    public void Problem_unknown_kind_fails(string kind) => Fails(Problem, Req<TripProblemRequest>(ProblemBody(), ("kind", kind)), "Kind");

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(14, true)]
    [InlineData(15, false)]
    [InlineData(-1, false)]
    public void Problem_day_range(int day, bool ok)
    {
        var req = Req<TripProblemRequest>(ProblemBody(), ("day", day));
        if (ok) Passes(Problem, req);
        else Fails(Problem, req, "Day");
    }

    [Fact]
    public void Problem_note_limit_is_300()
    {
        Passes(Problem, Req<TripProblemRequest>(ProblemBody(), ("note", new string('a', 300))));
        Fails(Problem, Req<TripProblemRequest>(ProblemBody(), ("note", new string('a', 301))), "Note");
    }

    [Fact]
    public void Problem_enquiry_id_is_optional_but_not_empty()
    {
        Passes(Problem, Req<TripProblemRequest>(ProblemBody(), ("enquiryId", null)));
        Fails(Problem, Req<TripProblemRequest>(ProblemBody(), ("enquiryId", Guid.Empty)), "EnquiryId");
    }

    [Theory]
    [InlineData(-90.0, true)]
    [InlineData(90.0, true)]
    [InlineData(29.38, true)]
    [InlineData(90.1, false)]
    [InlineData(91.0, false)]
    [InlineData(-91.0, false)]
    public void Problem_latitude_range(double lat, bool ok)
    {
        var req = Req<TripProblemRequest>(ProblemBody(), ("latitude", lat));
        if (ok) Passes(Problem, req);
        else Fails(Problem, req, "Latitude");
    }

    [Theory]
    [InlineData(-180.0, true)]
    [InlineData(180.0, true)]
    [InlineData(79.46, true)]
    [InlineData(180.1, false)]
    [InlineData(-181.0, false)]
    public void Problem_longitude_range(double lon, bool ok)
    {
        var req = Req<TripProblemRequest>(ProblemBody(), ("longitude", lon));
        if (ok) Passes(Problem, req);
        else Fails(Problem, req, "Longitude");
    }

    [Fact]
    public void Report_traveller_valid_passes() =>
        Passes(ReportTraveller, Req<ReportTravellerRequest>(new() { ["reasonId"] = Guid.NewGuid(), ["details"] = "Shouted on the phone" }));

    [Fact]
    public void Report_traveller_needs_a_reason()
    {
        Fails(ReportTraveller, Req<ReportTravellerRequest>(new() { ["details"] = "x" }), "ReasonId");
        Fails(ReportTraveller, Req<ReportTravellerRequest>(new() { ["reasonId"] = Guid.Empty }), "ReasonId");
    }

    [Fact]
    public void Report_traveller_details_limit_is_500()
    {
        var id = Guid.NewGuid();
        Passes(ReportTraveller, Req<ReportTravellerRequest>(new() { ["reasonId"] = id, ["details"] = new string('a', 500) }));
        Passes(ReportTraveller, Req<ReportTravellerRequest>(new() { ["reasonId"] = id, ["details"] = null }));
        Fails(ReportTraveller, Req<ReportTravellerRequest>(new() { ["reasonId"] = id, ["details"] = new string('a', 501) }), "Details");
    }

    [Fact]
    public void Mute_request_binds_both_values()
    {
        Assert.True(Req<MuteTripRequest>(new() { ["muted"] = true }).Muted);
        Assert.False(Req<MuteTripRequest>(new() { ["muted"] = false }).Muted);
        Assert.False(Req<MuteTripRequest>(new()).Muted);
    }
}
