using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;

namespace RentNearBy.Tests.Owner.Be3;

// AU-5 field matrix: on an Approved stay the review fields (name, registration, GSTIN, location, district, city) wait for the admin;
// everything else saves at once.
public class StayLiveEditMatrixTests
{
    private static readonly Guid TypeA = Guid.NewGuid(), TypeB = Guid.NewGuid(), District = Guid.NewGuid(), City = Guid.NewGuid();

    internal static StayReviewValues Live() => new("Lake View Stay", TypeA, "Homestay registration", null, "UK-123", null,
        29.39m, 79.45m, "Mall Road", District, "Nainital", City, "Nainital");

    [Theory]
    [InlineData(StayReviewFields.Name)]
    [InlineData(StayReviewFields.RegistrationType)]
    [InlineData(StayReviewFields.RegistrationOtherName)]
    [InlineData(StayReviewFields.RegistrationNumber)]
    [InlineData(StayReviewFields.Gstin)]
    [InlineData(StayReviewFields.Location)]
    [InlineData(StayReviewFields.Address)]
    [InlineData(StayReviewFields.District)]
    [InlineData(StayReviewFields.City)]
    public void Each_review_field_goes_to_the_pending_change_alone(string field)
    {
        var live = Live();
        var asked = field switch
        {
            StayReviewFields.Name => live with { Name = "Lake View Cottage" },
            StayReviewFields.RegistrationType => live with { RegistrationTypeId = TypeB, RegistrationTypeName = "Udyam" },
            StayReviewFields.RegistrationOtherName => live with { RegistrationOtherName = "Panchayat" },
            StayReviewFields.RegistrationNumber => live with { RegistrationNumber = "UK-999" },
            StayReviewFields.Gstin => live with { Gstin = "05ABCDE1234F1Z5" },
            StayReviewFields.Location => live with { Latitude = 29.40m },
            StayReviewFields.Address => live with { Address = "Tallital" },
            StayReviewFields.District => live with { DistrictId = Guid.NewGuid(), DistrictName = "Almora" },
            _ => live with { CityId = Guid.NewGuid(), CityName = "Bhimtal" },
        };
        Assert.Equal([field], StayReviewFields.Diff(live, asked));
        Assert.Equal(StayReviewFields.SaveOutcome.NewPending, StayReviewFields.Decide(live, asked, null));
    }

    [Fact]
    public void Non_review_fields_never_reach_the_review_values()
    {
        // Stay type, vehicle access, host lives here, facilities, description, rooms, photos, rules: not part of StayReviewValues.
        var names = typeof(StayReviewValues).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var direct in new[] { "StayTypeId", "VehicleAccess", "HostLivesHere", "Facilities", "Description", "CheckInTime", "PricePerNight" })
            Assert.DoesNotContain(direct, names);
        var handler = Be3Kit.Source("RentNearBy.Api", "Handlers", "StayHandlers.cs");
        var live = handler[handler.IndexOf("private static async Task<IResult> SaveLiveBasicAsync", StringComparison.Ordinal)..];
        live = live[..live.IndexOf("public static async Task<IResult> UploadPhoto", StringComparison.Ordinal)];
        Assert.Contains("stay.Description = request.Description;", live);
        Assert.DoesNotContain("stay.Name =", live);
        Assert.DoesNotContain("stay.Latitude =", live);
        Assert.DoesNotContain("stay.RegistrationNumber =", live);
    }

    [Fact]
    public void Rooms_photos_and_rules_save_at_once_on_a_live_stay_and_review_keeps_them_locked()
    {
        var handler = Be3Kit.Source("RentNearBy.Api", "Handlers", "StayHandlers.cs");
        Assert.Contains("private static bool IsLiveEditable(StayListing s) => s.Status != StayStatuses.PendingReview;", handler);
        foreach (var method in new[] { "UploadPhoto", "DeletePhoto", "UpsertRooms", "UpsertRules" })
        {
            var body = handler[handler.IndexOf($"public static async Task<IResult> {method}(", StringComparison.Ordinal)..];
            var end = body.IndexOf("public static async Task<IResult>", 10, StringComparison.Ordinal);
            body = end > 0 ? body[..end] : body;
            Assert.Contains("if (!IsLiveEditable(stay)) return LockedResponse(stay);", body);
        }
        Assert.Contains("stay.Photos.Count <= 1) return OwnerHttp.Refuse(OwnerErrorTypes.StayNeedsPhoto)", handler);
    }
}

// AU-5 states: none -> Pending -> Approved / Rejected -> a new save starts Pending again; one pending change; the strip shows 7 days.
public class StayPendingEditStateTests
{
    [Fact]
    public void A_save_equal_to_live_clears_and_the_same_save_again_writes_nothing()
    {
        var live = StayLiveEditMatrixTests.Live();
        Assert.Equal(StayReviewFields.SaveOutcome.ClearPending, StayReviewFields.Decide(live, live, null));
        var asked = live with { Name = "New name" };
        var pending = new StayPendingEdit(StayReviewFields.Diff(live, asked), asked);
        Assert.Equal(StayReviewFields.SaveOutcome.Unchanged, StayReviewFields.Decide(live, asked, pending));
        Assert.Equal(StayReviewFields.SaveOutcome.NewPending, StayReviewFields.Decide(live, asked with { Address = "x" }, pending));
        Assert.Equal(StayReviewFields.SaveOutcome.ClearPending, StayReviewFields.Decide(live, live, pending));
    }

    [Fact]
    public void Approve_applies_only_the_changed_fields()
    {
        var stay = new StayListing
        {
            Name = "Old", RegistrationTypeId = Guid.NewGuid(), RegistrationTypeName = "A", RegistrationNumber = "N1", Address = "Old road",
            Latitude = 1, Longitude = 2,
        };
        var live = new StayReviewValues(stay.Name, stay.RegistrationTypeId, stay.RegistrationTypeName, null, stay.RegistrationNumber, null,
            stay.Latitude, stay.Longitude, stay.Address, null, null, null, null);
        var asked = live with { Name = "New", Latitude = 3, Longitude = 4 };
        StayPendingEditService.Apply(stay, new StayPendingEdit(StayReviewFields.Diff(live, asked), asked));
        Assert.Equal(("New", 3m, 4m, "Old road", "N1"), (stay.Name, stay.Latitude!.Value, stay.Longitude!.Value, stay.Address, stay.RegistrationNumber));
    }

    [Fact]
    public void The_result_strip_shows_seven_days_and_never_over_a_newer_pending_change()
    {
        var now = new DateTime(2026, 11, 21, 6, 0, 0, DateTimeKind.Utc);
        Assert.True(StayReviewFields.StripVisible(StayEditResults.Approved, now.AddDays(-6), false, now));
        Assert.False(StayReviewFields.StripVisible(StayEditResults.Approved, now.AddDays(-8), false, now));
        Assert.False(StayReviewFields.StripVisible(StayEditResults.Rejected, now.AddDays(-1), true, now));
        Assert.False(StayReviewFields.StripVisible(null, now, false, now));
    }

    [Fact]
    public void Pending_json_round_trips_and_a_bad_value_reads_as_nothing_waiting()
    {
        var live = StayLiveEditMatrixTests.Live();
        var asked = live with { Gstin = null, Name = "Renamed" };
        var json = StayPendingEditJson.Write(new StayPendingEdit(StayReviewFields.Diff(live, asked), asked));
        var back = StayPendingEditJson.Read(json)!;
        Assert.Equal(["name"], back.Fields);
        Assert.Equal("Renamed", back.Asked.Name);
        Assert.Null(StayPendingEditJson.Read("{not json"));
        Assert.Null(StayPendingEditJson.Read("{\"fields\":[],\"asked\":null}"));
        Assert.Null(StayPendingEditJson.Read(null));
    }

    [Fact]
    public void The_admin_echo_is_compared_at_database_precision()
    {
        var stored = new DateTime(2026, 11, 21, 6, 0, 0, 123, DateTimeKind.Utc).AddTicks(4560);
        Assert.True(StayPendingEditService.SameInstant(stored, stored));
        Assert.True(StayPendingEditService.SameInstant(stored, DateTime.SpecifyKind(stored, DateTimeKind.Unspecified)));
        Assert.False(StayPendingEditService.SameInstant(stored, stored.AddMilliseconds(1)));
    }

    [Fact]
    public void Leaving_approved_drops_the_pending_change()
    {
        var handler = Be3Kit.Source("RentNearBy.Api", "Handlers", "StayHandlers.cs");
        var delete = handler[handler.IndexOf("public static async Task<IResult> DeleteMine(", StringComparison.Ordinal)..];
        Assert.Contains("stay.PendingEditJson = null;", delete);
        var service = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "StayPendingEditService.cs");
        Assert.Contains("if (stay.Status != StayStatuses.Approved)", service);
        Assert.Contains("return StayEditDecision.Fail(OwnerErrorTypes.StayEditGone);", service);
        Assert.Contains("return StayEditDecision.Fail(OwnerErrorTypes.StayEditChanged);", service);
    }
}
