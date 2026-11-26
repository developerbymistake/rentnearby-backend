using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.Models.Social;

namespace RentNearBy.Tests.Social;

// Contract 3.1 / 3.2: every ChatHub method starting with "Dm" is covered by SocialHubFilter; DM events go only to dm_ groups.
public class SocialHubGuardTests
{
    private static IEnumerable<MethodInfo> HubMethods() => typeof(ChatHub)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => !m.IsSpecialName && m.DeclaringType == typeof(ChatHub));

    [Fact]
    public void The_dm_methods_are_exactly_send_and_read()
        => Assert.Equal(new[] { "DmRead", "DmSend" }, HubMethods().Select(m => m.Name).Where(SocialHubFilter.IsSocialMethod).OrderBy(n => n).ToArray());

    [Fact]
    public void Every_dm_method_is_covered_by_the_filter()
        => Assert.All(HubMethods().Where(m => m.Name.StartsWith("Dm", StringComparison.Ordinal)), m => Assert.True(SocialHubFilter.IsSocialMethod(m.Name)));

    [Fact]
    public void Non_dm_methods_are_untouched_by_the_filter()
    {
        Assert.False(SocialHubFilter.IsSocialMethod("JoinConversation"));
        Assert.False(SocialHubFilter.IsSocialMethod("LeaveConversation"));
    }

    [Fact]
    public void Dm_events_target_dm_groups()
    {
        var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
        Assert.Equal($"dm_{id}", SocialHubEventNames.Group(id));
        var src = File.ReadAllText(Path.Combine(Buddy.Foundation.BuddySources.Root(), "RentNearBy.Api", "Hubs", "SocialHubEvents.cs"));
        Assert.Contains("SocialHubEventNames.Group(e.UserId)", src);
        Assert.DoesNotContain("user_", src);
    }

    [Fact]
    public void Filter_is_a_hub_filter() => Assert.True(typeof(IHubFilter).IsAssignableFrom(typeof(SocialHubFilter)));
}
