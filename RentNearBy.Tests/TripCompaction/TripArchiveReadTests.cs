using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;

namespace RentNearBy.Tests.TripCompaction;

// Trip compaction, read and refusal side (CONTRACT.md): every trip answer carries archived + summary, the route line comes from
// the summary, PlanJson "{}" never crashes, and a move that needs the plan answers 409 TripArchived.
public class TripArchiveReadTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateOnly Start = new(2026, 5, 10);

    private static readonly TripArchiveSummary Kept = new()
    {
        Title = "Nainital & Mukteshwar", StartDate = Start, EndDate = Start.AddDays(3), DayCount = 4, SpotCount = 9,
        Cities =
        [
            new TripArchiveCity { Name = "Nainital", Nights = 2, Spots = ["Naini Lake", "Snow View Point"], SpotCount = 5, Stays = ["Lake Inn"] },
            new TripArchiveCity { Name = "Mukteshwar", Nights = 1, Spots = ["Chauli ki Jali"], SpotCount = 1 },
        ],
        TaxiBooked = true, MemberCount = 3, ExpensesTotal = 12450,
    };

    private static Trip Archived(string? summaryJson = null) => new()
    {
        Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Title = "Nainital & Mukteshwar", Status = TripStatuses.Completed, CityKey = 210000101,
        CityName = "Nainital", CityLatitude = 29.39, CityLongitude = 79.45, PlanJson = TripArchive.EmptyPlanJson, DayCount = 4, SpotCount = 9,
        StartDate = Start, EndDate = Start.AddDays(3), CompletedAt = DateTime.UtcNow.AddDays(-100), ArchivedAt = DateTime.UtcNow,
        SummaryJson = summaryJson ?? TripArchive.Serialize(Kept), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static Trip Live() => new()
    {
        Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Title = "Live", Status = TripStatuses.Planned, CityKey = 210000101,
        CityName = "Nainital", PlanJson = "{}", DayCount = 0, SpotCount = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static (int Status, string? Type) Error(IResult? r)
    {
        Assert.NotNull(r);
        var status = ((IStatusCodeHttpResult)r!).StatusCode ?? 200;
        var type = (((IValueHttpResult)r).Value as ApiResponse<object>)?.Error?.Type;
        return (status, type);
    }

    [Fact]
    public void Trip_dto_of_an_archived_trip_carries_archived_summary_and_the_summary_route()
    {
        var trip = Archived();
        var dto = TripHandlers.ToDto(trip, TripPlanReader.Read(trip));

        Assert.True(dto.Archived);
        Assert.Equal(trip.ArchivedAt, dto.ArchivedAt);
        Assert.NotNull(dto.Summary);
        Assert.Equal(["Nainital", "Mukteshwar"], dto.LegCities);
        Assert.Equal(2, dto.LegCount);
        Assert.NotNull(dto.Plan);
        Assert.Equal(12450, dto.Summary!.ExpensesTotal);

        var json = JsonSerializer.Serialize(dto, Web);
        Assert.Contains("\"archived\":true", json);
        Assert.Contains("\"archivedAt\":", json);
        Assert.Contains("\"summary\":{", json);
        Assert.Contains("\"cities\":[", json);
    }

    [Fact]
    public void Trip_dto_of_a_live_trip_says_not_archived_and_no_summary()
    {
        var trip = Live();
        var dto = TripHandlers.ToDto(trip, TripPlanReader.Read(trip));
        Assert.False(dto.Archived);
        Assert.Null(dto.ArchivedAt);
        Assert.Null(dto.Summary);
        var json = JsonSerializer.Serialize(dto, Web);
        Assert.Contains("\"archived\":false", json);
        Assert.Contains("\"summary\":null", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void Archived_trip_with_a_missing_or_bad_summary_still_answers_with_the_row_city(string? summaryJson)
    {
        var trip = Archived();
        trip.SummaryJson = summaryJson;
        var dto = TripHandlers.ToDto(trip, TripPlanReader.Read(trip));
        Assert.True(dto.Archived);
        Assert.Null(dto.Summary);
        Assert.Equal(["Nainital"], dto.LegCities);
        Assert.Equal(1, dto.LegCount);
    }

    [Fact]
    public void Viewer_projection_of_an_archived_trip_has_no_legs_or_days_and_the_summary()
    {
        var trip = Archived();
        var view = TripViewerProjection.Build(new TripViewerSource(
            trip, TripPlanReader.Read(trip), TripSharingRoles.Viewer, "Rahul", null, 3, [], [], null, null, Start.AddDays(200)));

        Assert.True(view.Archived);
        Assert.NotNull(view.Summary);
        Assert.Empty(view.Legs);
        Assert.Empty(view.Days);
        Assert.Equal(["Nainital", "Mukteshwar"], view.LegCities);
    }

    [Fact]
    public void Viewer_projection_of_a_live_trip_keeps_its_legs_and_no_summary()
    {
        var trip = Live();
        var view = TripViewerProjection.Build(new TripViewerSource(
            trip, TripPlanReader.Read(trip), TripSharingRoles.Viewer, "Rahul", null, 1, [], [], null, null, Start));
        Assert.False(view.Archived);
        Assert.Null(view.Summary);
        Assert.Single(view.Legs);
    }

    [Fact]
    public void Viewer_summary_json_never_names_a_forbidden_field()
    {
        var trip = Archived();
        var view = TripViewerProjection.Build(new TripViewerSource(
            trip, TripPlanReader.Read(trip), TripSharingRoles.Viewer, "Rahul", null, 3, [], [], null, null, Start.AddDays(200)));
        var summary = JsonSerializer.Serialize(view.Summary, Web);
        foreach (var f in new[] { "phone", "price", "fare", "conversationId", "listingId", "planJson", "inviteCode", "upi" })
            Assert.DoesNotContain(f, summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guard_answers_trip_archived_before_trip_completed()
    {
        var (status, type) = Error(TripGuards.TripNotCompleted(Archived()));
        Assert.Equal(409, status);
        Assert.Equal(TripErrorTypes.TripArchived, type);

        var planned = Archived();
        planned.Status = TripStatuses.Planned;
        Assert.Equal(TripErrorTypes.TripArchived, Error(TripGuards.TripNotCompleted(planned)).Type);

        Assert.Null(TripGuards.TripNotCompleted(Live()));
        Assert.Equal(TripEnquiryErrorTypes.TripCompleted, Error(TripGuards.TripNotCompleted(new Trip { Status = TripStatuses.Completed })).Type);
    }

    [Fact]
    public void Archived_refusal_is_a_typed_409()
    {
        var (status, type) = Error(TripHandlers.TripArchivedResponse());
        Assert.Equal(409, status);
        Assert.Equal("TripArchived", type);
        Assert.Equal(409, SocialErrorTypes.StatusOf(SocialErrorTypes.TripArchived));
        Assert.Equal(TripErrorTypes.TripArchived, SocialErrorTypes.TripArchived);
    }

    [Fact]
    public void Empty_plan_never_crashes_the_readers()
    {
        var trip = Archived();
        var plan = TripPlanReader.Read(trip);
        Assert.Single(plan.Legs!);
        Assert.Empty(plan.Days);
        Assert.InRange(TripViewerProjection.Progress(trip, plan, [], null), 0, 100);
    }
}
