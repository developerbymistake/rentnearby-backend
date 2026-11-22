using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// JT-B-LC-4 / JT-B-LC-5: closure before purge (FB-22) and the token copy of the last pushes (FB-23).
public class RoleAndAccountJourneyTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    [Theory]
    [InlineData("RentNearBy.Infrastructure/Services/AccountDeletionService.cs", "OnListingsRemovedAsync(ListingKinds.Taxi", "PurgeUserListingsAsync(userId)")]
    [InlineData("RentNearBy.Api/Handlers/AdminRoleHandlers.cs", "tripClosure.OnListingsRemovedAsync(purgeKind", "listingPurge.DeleteUserListingRowsAsync(user.Id, purgeKind)")]
    [InlineData("RentNearBy.Api/Handlers/RoleHandlers.cs", "tripClosure.OnListingsRemovedAsync(leftKind", "listingPurge.DeleteUserListingRowsAsync(userId, leftKind)")]
    public void Closure_runs_before_the_purge(string file, string closure, string purge)
    {
        var src = Src(file.Split('/'));
        var c = src.IndexOf(closure, StringComparison.Ordinal);
        var p = src.IndexOf(purge, StringComparison.Ordinal);
        Assert.True(c > 0 && p > c, $"{file}: closure must come before the purge");
    }

    [Fact]
    public void Delivery_prefers_the_token_copy()
    {
        var push = new OutboxPushPayload("ringend:x", Guid.NewGuid(), null, "t", "b", null, null, "tag", TargetTokens: ["a", "b", "a"]);
        Assert.Equal(["a", "b"], PushTargets.Tokens(push, []));
        var plain = push with { TargetTokens = null, ExceptToken = "c" };
        Assert.Equal(["d"], PushTargets.Tokens(plain, ["c", "d"]));
    }

    [Fact]
    public void Stamp_copies_tokens_into_added_ring_end_rows_of_that_user_only()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite()).Options);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var me = Guid.NewGuid();
        OutboxMessage Row(string key, Guid user) => new()
        {
            Key = key, Exchange = "rnb.push", RoutingKey = "send", CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow,
            Payload = JsonSerializer.Serialize(new OutboxPushPayload(key, user, null, "t", "b", null, null, key), json),
        };
        var mine = Row("ringend:1", me);
        var other = Row("ringend:2", Guid.NewGuid());
        var notRingEnd = Row("ev:3", me);
        db.OutboxMessages.AddRange(mine, other, notRingEnd);

        Assert.Equal(1, PushTokenSnapshot.Stamp(db, me, ["tok"], k => k.StartsWith("ringend:", StringComparison.Ordinal)));
        Assert.Equal(["tok"], JsonSerializer.Deserialize<OutboxPushPayload>(mine.Payload, json)!.TargetTokens);
        Assert.Null(JsonSerializer.Deserialize<OutboxPushPayload>(other.Payload, json)!.TargetTokens);
        Assert.Null(JsonSerializer.Deserialize<OutboxPushPayload>(notRingEnd.Payload, json)!.TargetTokens);
    }

    [Fact]
    public void Suspend_and_role_change_copy_tokens_before_deleting_them()
    {
        foreach (var file in new[] { "AdminHandlers.cs", "AdminRoleHandlers.cs" })
        {
            var src = Src("RentNearBy.Api", "Handlers", file);
            var stamp = src.IndexOf("PushTokenSnapshot.Stamp(", StringComparison.Ordinal);
            var delete = src.IndexOf("DeviceTokenCleanup.DeleteAllAsync", StringComparison.Ordinal);
            Assert.True(stamp > 0 && delete > stamp, file);
        }
        Assert.Contains("SendGoLiveNotificationAsync(hubContext, publisher, notification, targetTokens: tokenCopy)", Src("RentNearBy.Api", "Handlers", "AdminRoleHandlers.cs"));
    }
}
