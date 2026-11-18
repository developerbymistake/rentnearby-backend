using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Tests.TaxiRequests;

public class TripInviteGuidTests
{
    private static Trip NewTrip(Guid? clientId = null)
    {
        var request = new SaveTripRequest
        {
            ClientTripId = clientId,
            Plan = new TripPlan
            {
                CityKey = 210000001,
                Legs = [new TripCityLeg { Index = 1, CityKey = 210000001, CityName = "Nainital", Latitude = 29.38, Longitude = 79.45 }],
                Days = [new TripDay { Day = 1, Spots = [] }],
            },
        };
        return TripHandlers.NewTrip(Guid.NewGuid(), request, request.Plan, DateTime.UtcNow);
    }

    [Fact]
    public void Create_sets_a_code_that_is_not_the_trip_id()
    {
        var client = Guid.NewGuid();
        var a = NewTrip(client);
        var b = NewTrip(client);
        Assert.NotEqual(Guid.Empty, a.InviteCode);
        Assert.NotEqual(a.Id, a.InviteCode);
        Assert.NotEqual(client, a.InviteCode);
        Assert.NotEqual(a.InviteCode, b.InviteCode);
    }

    [Fact]
    public void Invite_dto_matches_the_sample()
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, "trip_invite.json")))!;
        var trip = new Trip
        {
            Id = Guid.Parse(sample["tripId"]!.GetValue<string>()),
            InviteCode = Guid.Parse(sample["inviteCode"]!.GetValue<string>()),
        };
        var dto = TripHandlers.InviteDto(trip, "https://app.example.com/");
        var actual = JsonSerializer.SerializeToNode(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(JsonNode.DeepEquals(sample, actual));
    }

    [Fact]
    public void Trip_dto_does_not_expose_the_code()
    {
        var dtos = typeof(TripDto).Assembly.GetTypes()
            .Where(t => t.IsClass && t.Namespace != null && t.Namespace.StartsWith("RentNearBy.Core.DTOs") && t != typeof(TripInviteDto))
            .Where(t => t.Name.Contains("Trip") || t.Name.Contains("Driver") || t.Name.Contains("Admin") || t.Name.Contains("Taxi"))
            .ToList();
        Assert.Contains(typeof(TripDto), dtos);
        Assert.Contains(typeof(TripSummaryDto), dtos);
        var offenders = dtos
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (t, p)))
            .Where(x => x.p.Name.Contains("Invite", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.t.Name}.{x.p.Name}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Link_builder_is_null_without_a_base_url()
    {
        var code = Guid.NewGuid();
        Assert.Null(TripInvites.Link(null, code));
        Assert.Null(TripInvites.Link("  ", code));
        Assert.Equal($"https://x.test/app?invite={code}", TripInvites.Link("https://x.test", code));
        Assert.Null(TripHandlers.InviteDto(new Trip { Id = Guid.NewGuid(), InviteCode = code }, null).InviteLink);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://files.test")]
    public void Unset_or_invalid_setting_uses_the_app_share_host(string? configured)
    {
        Assert.Equal(TripInvites.DefaultPublicBaseUrl, TripInvites.PublicBaseUrl(configured));
        var code = Guid.NewGuid();
        Assert.Equal($"https://developerbymistake.tech/app?invite={code}", TripInvites.Link(TripInvites.PublicBaseUrl(configured), code));
    }

    [Fact]
    public void Configured_setting_wins()
    {
        Assert.Equal("https://staging.test/", TripInvites.PublicBaseUrl(" https://staging.test/ "));
        var code = Guid.NewGuid();
        Assert.Equal($"https://staging.test/app?invite={code}", TripInvites.Link(TripInvites.PublicBaseUrl("https://staging.test/"), code));
    }

    [Fact]
    public void Default_host_matches_the_app_share_page()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var program = File.ReadAllText(Path.Combine(dir.FullName, "RentNearBy.Api", "Program.cs"));
        Assert.Contains($"\"{TripInvites.DefaultPublicBaseUrl}/app\"", program);
    }
}
