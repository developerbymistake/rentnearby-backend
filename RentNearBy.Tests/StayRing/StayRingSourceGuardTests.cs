using System.Text.RegularExpressions;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.StayRing;

// Source guards of the stay enquiry ring: where it hooks in, that it never changes an enquiry, and that every existing Stay path
// (expiry, nudges, statuses, replies) is left exactly as it was (SPEC OWNER CLARIFICATION, use case 4).
public class StayRingSourceGuardTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    private static readonly string[][] RingFiles =
    [
        ["RentNearBy.Infrastructure", "Services", "StayRing", "StayRingWriter.cs"],
        ["RentNearBy.Infrastructure", "Services", "StayRing", "StayRingEndWatcherService.cs"],
        ["RentNearBy.Api", "Handlers", "StayRingHandlers.cs"],
    ];

    // Use case 1: the ring rows are staged in the enquiry's own transaction, for new rows only (a widened row is not a new enquiry).
    [Fact]
    public void Ring_is_staged_once_inside_the_create_transaction()
    {
        var src = Src("RentNearBy.Api", "Handlers", "TripEnquiryHandlers.cs");
        var call = "await StayRingWriter.StageRingsAsync(db, rings, now);";
        Assert.Single(Regex.Matches(src, Regex.Escape("StayRingWriter.")));
        var save = src.IndexOf("var lost = await TripBulk.SaveRowsAsync", StringComparison.Ordinal);
        var begin = src.LastIndexOf("await unitOfWork.BeginTransactionAsync();", save, StringComparison.Ordinal);
        var stage = src.IndexOf(call, StringComparison.Ordinal);
        var commit = src.IndexOf("await unitOfWork.CommitTransactionAsync();", stage, StringComparison.Ordinal);
        var wake = src.IndexOf("outbox.Wake();", stage, StringComparison.Ordinal);
        Assert.True(begin > 0 && begin < save && save < stage && stage < commit && commit < wake, $"{begin} {save} {stage} {commit} {wake}");
        Assert.Contains("TripBulk.StagePush(db, outbox, TripNotifications.TripEnquiryReceived(e, pushContext), e.Id, now, live);", src);
    }

    // Use cases 5, 6: the ring code reads enquiries without tracking and never writes one.
    [Fact]
    public void Ring_code_never_changes_an_enquiry()
    {
        foreach (var parts in RingFiles)
        {
            var src = Src(parts);
            var name = parts[^1];
            Assert.DoesNotMatch(@"\.Status\s*=(?!=)", src);
            Assert.DoesNotMatch(@"ExpiresAt\s*=(?!=)", src);
            Assert.DoesNotMatch(@"UpdatedAt\s*=(?!=)", src);
            Assert.DoesNotContain("UPDATE \"TripEnquiries\"", src);
            Assert.DoesNotContain("TripEnquiries.Update", src);
            Assert.DoesNotContain("GetByIdTrackedAsync", src);
            foreach (Match m in Regex.Matches(src, @"db\.TripEnquiries\.[A-Za-z]+"))
                Assert.True(m.Value == "db.TripEnquiries.AsNoTracking", $"{name}: {m.Value}");
        }
    }

    // Use case 3 + OWNER CLARIFICATION: no quiet hours, no daily cap, no new hold: none of the night or cap helpers is used.
    [Fact]
    public void Ring_has_no_quiet_hours_and_no_daily_cap()
    {
        var all = string.Concat(RingFiles.Select(Src)) + Src("RentNearBy.Core", "Models", "StayRing", "StayRingPayload.cs");
        Assert.DoesNotContain("TripQuietHours", all);
        Assert.DoesNotContain("TripQuietRules", all);
        Assert.DoesNotContain("IsNight", all);
        Assert.DoesNotContain("DailyCap", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IstOffset", all);
    }

    // Use case 4: the Stay paths that own expiry, nudges, statuses and replies hold no stay ring code at all.
    [Theory]
    [InlineData("RentNearBy.Infrastructure/Services/TripTimerService.cs")]
    [InlineData("RentNearBy.Infrastructure/Services/TripReminderService.cs")]
    [InlineData("RentNearBy.Api/Handlers/TripHostGuards.cs")]
    [InlineData("RentNearBy.Core/Models/TripNotifications.cs")]
    [InlineData("RentNearBy.Core/Models/TripExecutionModels.cs")]
    [InlineData("RentNearBy.Api/Handlers/TripShortlistHandlers.cs")]
    public void Existing_stay_paths_hold_no_ring_code(string file)
    {
        var src = Src(file.Split('/'));
        Assert.DoesNotContain("StayRing", src);
        Assert.DoesNotContain("StayEnquiryRing", src);
    }

    // Use case 16: suspend and role change end a live ring with the token copy, before the tokens are deleted.
    [Theory]
    [InlineData("AdminHandlers.cs")]
    [InlineData("AdminRoleHandlers.cs")]
    public void Suspend_and_role_change_end_live_rings_before_the_tokens_go(string file)
    {
        var src = Src("RentNearBy.Api", "Handlers", file);
        var end = src.IndexOf("StayRingWriter.EndLiveRingsOfOwnerAsync(db,", StringComparison.Ordinal);
        var stamp = src.IndexOf("PushTokenSnapshot.Stamp(", StringComparison.Ordinal);
        var delete = src.IndexOf("DeviceTokenCleanup.DeleteAllAsync", StringComparison.Ordinal);
        Assert.True(end > 0 && end < stamp && stamp < delete, $"{file}: {end} {stamp} {delete}");
        Assert.Contains("StayRingWriter.IsRingEndKey(k)", src[stamp..delete]);
    }

    [Fact]
    public void Watcher_is_a_hosted_service_and_the_routes_keep_the_stay_owner_policy()
    {
        Assert.Contains("services.AddHostedService<RentNearBy.Infrastructure.Services.StayRing.StayRingEndWatcherService>();",
            Src("RentNearBy.Api", "Extensions", "ServiceCollectionExtensions.cs"));
        var endpoints = Src("RentNearBy.Api", "Endpoints", "StayEndpoints.cs");
        var lines = endpoints.Split('\n').Where(l => l.Contains("StayRingHandlers.", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, lines.Count);
        Assert.All(lines, l => Assert.Contains(".RequireAuthorization(\"CanListStays\")", l));
    }

    // The push pipeline picks ring shapes through RingPushTypes for both kinds; the taxi constants are still the ones in use.
    [Fact]
    public void Pipeline_generalises_by_type_not_by_copy()
    {
        var worker = Src("RentNearBy.Infrastructure", "Services", "PushDeliveryWorkerService.cs");
        Assert.Contains("if (RingPushTypes.IsRing(type))", worker);
        Assert.Contains("else if (RingPushTypes.IsRingEnd(type)) data.Remove(\"key\");", worker);
        var fcm = Src("RentNearBy.Infrastructure", "Services", "FcmService.cs");
        Assert.Contains("RingEndMessage(spec, TaxiPushKinds.UpdatesChannelId, RingEndTtl)", fcm);
        Assert.Single(Regex.Matches(fcm, Regex.Escape("private static Message RingMessage(")));
    }
}
