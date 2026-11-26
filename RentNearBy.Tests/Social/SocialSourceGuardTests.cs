using System.Reflection;
using System.Text.RegularExpressions;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Social;

// Contract 1.7, 3.6, 6.10: chat statements carry the membership predicate, no direct push or FCM, no await in a loop.
public class SocialSourceGuardTests
{
    private static IReadOnlyList<string> Files()
    {
        var root = BuddySources.Root();
        return new[]
            {
                Path.Combine(root, "RentNearBy.Infrastructure", "Services", "Social"),
                Path.Combine(root, "RentNearBy.Api", "Handlers", "Social"),
            }
            .Where(Directory.Exists)
            .SelectMany(f => Directory.GetFiles(f, "*.cs", SearchOption.AllDirectories))
            .ToList();
    }

    [Fact]
    public void Folders_exist() => Assert.True(Files().Count >= 10);

    [Fact]
    public void No_await_inside_a_loop_without_batch_loop()
    {
        var hits = Files().SelectMany(BuddySources.AwaitInLoops).ToList();
        Assert.True(hits.Count == 0, string.Join("\n", hits));
    }

    [Fact]
    public void No_direct_notification_event_or_fcm()
    {
        var rx = new Regex(@"new\s+NotificationEvent\b|IFcmService|IChatFcmService|NotificationEvents\.(Add|AddRange)");
        var hits = Files().Where(f => rx.IsMatch(File.ReadAllText(f))).Select(BuddySources.Rel).ToList();
        Assert.True(hits.Count == 0, string.Join("\n", hits));
    }

    // Every SQL text of SocialChatService that touches the chat tables for a user carries the membership predicate
    // (UnreadSql reads the callers' own member rows by UserId; InsertRoomSql creates the room before any member exists).
    [Fact]
    public void Chat_statements_carry_the_membership_predicate()
    {
        var fields = typeof(SocialChatService).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.Name.EndsWith("Sql", StringComparison.Ordinal) && f.Name is not ("UnreadSql" or "InsertRoomSql"))
            .Select(f => (f.Name, Sql: (string)f.GetValue(null)!))
            .Where(x => x.Sql.Contains("\"ChatMessages\"") || x.Sql.Contains("\"ChatRooms\"") || x.Sql.Contains("\"ChatRoomMembers\""))
            .ToList();
        Assert.NotEmpty(fields);
        foreach (var (name, sql) in fields)
            Assert.True(sql.Contains("\"ChatRoomMembers\"") && sql.Contains("@me"), name);
    }

    [Fact]
    public void Hub_statements_recheck_the_live_seeker_role()
    {
        foreach (var name in new[] { "BumpSql", "ReadSql" })
        {
            var sql = (string)typeof(SocialChatService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Assert.Contains(SocialChatService.SeekerSql, sql);
        }
        Assert.Contains("\"IsActive\"", SocialChatService.SeekerSql);
        Assert.Contains(RentNearBy.Core.Models.TravellerOnly.SeekerRoleSql, SocialChatService.SeekerSql);
    }

    [Fact]
    public void Trip_save_refreshes_the_post_snapshot()
    {
        var src = File.ReadAllText(Path.Combine(BuddySources.Root(), "RentNearBy.Api", "Handlers", "TripHandlers.cs"));
        var update = src[src.IndexOf("public static async Task<IResult> Update(", StringComparison.Ordinal)..];
        Assert.Contains("PublicTripService.RefreshPostSnapshot(trip, plan, postState)", update[..update.IndexOf("trip.UpdatedAt = now;", StringComparison.Ordinal)]);
    }
}
