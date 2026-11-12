using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

public class TripReminderInboxTests
{
    private static readonly DateOnly Today = new(2026, 11, 12);
    private static readonly DateTime Now = new(2026, 11, 12, 2, 30, 0, DateTimeKind.Utc);

    private static ReminderRow Waiting(Guid host, Guid id) => new()
    {
        UserId = host, Id = id, Name = "Asha", At = Now.AddDays(-2), On = new DateOnly(2026, 12, 1), Kind = TripEnquiryKinds.Stay,
    };

    [Fact]
    public void Host_plan_becomes_one_inbox_row_that_matches_the_push()
    {
        var host = Guid.NewGuid();
        var plan = TripReminderPlanner.PlanHosts(Today, [Waiting(host, Guid.NewGuid())]).Single();

        var row = TripReminderInbox.ForHosts([plan], Now).Single();

        Assert.Equal(host, row.TargetUserId);
        Assert.Equal(NotificationTypes.TripReminder, row.Type);
        Assert.Equal(plan.Push.Title, row.Title);
        Assert.Equal(plan.Push.Body, row.Body);
        Assert.Equal(plan.Push.Route, row.ActionRoute);
        Assert.Equal(plan.Push.ArgsJson, row.ActionArgumentsJson);
        Assert.Equal(Now, row.CreatedAt);
    }

    [Fact]
    public void Row_id_is_stable_for_the_same_push_key_so_a_retry_never_duplicates()
    {
        var host = Guid.NewGuid();
        var a = TripReminderPlanner.PlanHosts(Today, [Waiting(host, Guid.NewGuid())]).Single();
        var b = TripReminderPlanner.PlanHosts(Today, [Waiting(host, Guid.NewGuid())]).Single();

        Assert.Equal(TripReminderInbox.RowId(a.Push.Key), TripReminderInbox.RowId(b.Push.Key));
        Assert.Equal(TripReminderInbox.ForHosts([a], Now)[0].Id, TripReminderInbox.ForHosts([b], Now.AddMinutes(5))[0].Id);
    }

    [Fact]
    public void Different_hosts_and_different_days_get_different_row_ids()
    {
        var h1 = Guid.NewGuid();
        var h2 = Guid.NewGuid();
        Assert.NotEqual(
            TripReminderInbox.RowId(TripReminderRules.HostKey(h1, Today)), TripReminderInbox.RowId(TripReminderRules.HostKey(h2, Today)));
        Assert.NotEqual(
            TripReminderInbox.RowId(TripReminderRules.HostKey(h1, Today)), TripReminderInbox.RowId(TripReminderRules.HostKey(h1, Today.AddDays(1))));
    }

    [Fact]
    public void Traveller_plans_get_no_inbox_row_here()
    {
        var plan = new ReminderPlan(Guid.NewGuid(), ReminderCategory.NoReply,
            new OutboxPushPayload("k", Guid.NewGuid(), null, "t", "b", "/r", "{}", "k"), [], []);

        Assert.Empty(TripReminderInbox.ForHosts([plan], Now));
    }

    [Fact]
    public void One_row_per_host_for_a_whole_chunk()
    {
        var rows = Enumerable.Range(0, 50).Select(_ => Waiting(Guid.NewGuid(), Guid.NewGuid())).ToList();
        var plans = TripReminderPlanner.PlanHosts(Today, rows);

        var inbox = TripReminderInbox.ForHosts(plans, Now);

        Assert.Equal(50, inbox.Count);
        Assert.Equal(50, inbox.Select(r => r.Id).Distinct().Count());
    }
}
