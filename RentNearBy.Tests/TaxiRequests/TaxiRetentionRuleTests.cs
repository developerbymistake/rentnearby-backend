using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Retention.GroupB;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.TaxiRequests;

// Written for PostgreSQL (RENTNEARBY_TEST_DB); skipped without it.
[Trait("Category", "Integration")]
public class TaxiRetentionRuleTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly DateOnly Today = IstTime.TodayIst(Now);

    private async Task RunAsync(string name)
    {
        var rule = new TaxiRequestRetentionRules().Create().Single(r => r.Name == name);
        await using var ctx = CreateContext();
        var days = rule.DefaultDays;
        var rc = new RetentionContext(ctx, Now, Now.AddDays(-days), Today, Today.AddDays(-days), days);
        while (await rule.DeleteBatchAsync(rc, 1000, CancellationToken.None) > 0) { }
    }

    private async Task<Guid> UserAsync()
    {
        await using var ctx = CreateContext();
        var id = Guid.NewGuid();
        ctx.Users.Add(new User
        {
            Id = id, PhoneNumber = "9" + Random.Shared.NextInt64(100_000_000, 999_999_999), Name = "Retention", IsActive = true, IsPhoneVerified = true,
            RoleId = UserRoles.SeekerId, CreatedAt = Now, UpdatedAt = Now,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> RequestAsync(string status, DateTime? closedAt)
    {
        var user = await UserAsync();
        await using var ctx = CreateContext();
        var trip = new Trip
        {
            Id = Guid.NewGuid(), OwnerUserId = user, CityKey = 210000001, CityName = "Nainital", Title = "T", Status = "Completed", PlanJson = "{}",
            CreatedAt = Now, UpdatedAt = Now,
        };
        ctx.Trips.Add(trip);
        var request = new TaxiRequest
        {
            Id = Guid.NewGuid(), TripId = trip.Id, TravellerUserId = user, Status = status, WantPickup = true, PickupCityKey = 210000001,
            DispatchCityKey = 210000001, MatchToCityKey = 210000001, Seats = 4, Travellers = 2,
            DateFrom = Today, DateTo = Today, CreatedAt = Now, UpdatedAt = Now, ClosedAt = closedAt,
        };
        ctx.Set<TaxiRequest>().Add(request);
        await ctx.SaveChangesAsync();
        return request.Id;
    }

    private async Task<Guid> DueAsync(string state, DateTime? doneAt)
    {
        await using var ctx = CreateContext();
        var row = new DueAction
        {
            Id = Guid.NewGuid(), Kind = DueActionKinds.OfferExpire, SubjectId = Guid.NewGuid(), DueAt = Now.AddDays(-200),
            State = state, CreatedAt = Now.AddDays(-200), DoneAt = doneAt,
        };
        ctx.Set<DueAction>().Add(row);
        await ctx.SaveChangesAsync();
        return row.Id;
    }

    private async Task<bool> RequestExistsAsync(Guid id)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<TaxiRequest>().AnyAsync(r => r.Id == id);
    }

    private async Task<bool> DueExistsAsync(Guid id)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<DueAction>().AnyAsync(r => r.Id == id);
    }

    [IntegrationTheory]
    [InlineData("Open")]
    [InlineData("Collecting")]
    [InlineData("Selected")]
    [InlineData("Booked")]
    [InlineData("InProgress")]
    public async Task Active_requests_are_never_selected(string status)
    {
        var id = await RequestAsync(status, null);
        await RunAsync("TaxiRequests");
        Assert.True(await RequestExistsAsync(id));
    }

    [IntegrationTheory]
    [InlineData("Cancelled")]
    [InlineData("Expired")]
    [InlineData("Completed")]
    public async Task Closed_requests_older_than_the_limit_are_selected(string status)
    {
        var old = await RequestAsync(status, Now.AddDays(-91));
        var recent = await RequestAsync(status, Now.AddDays(-89));
        await RunAsync("TaxiRequests");
        Assert.False(await RequestExistsAsync(old));
        Assert.True(await RequestExistsAsync(recent));
    }

    [IntegrationTheory]
    [InlineData("Pending")]
    [InlineData("Running")]
    public async Task Due_actions_rule_keeps_pending_and_running_rows(string state)
    {
        var id = await DueAsync(state, null);
        await RunAsync("DueActions");
        Assert.True(await DueExistsAsync(id));
    }

    [IntegrationTheory]
    [InlineData("Done")]
    [InlineData("Skipped")]
    [InlineData("Cancelled")]
    public async Task Done_skipped_and_cancelled_due_actions_are_purged_after_14_days(string state)
    {
        var old = await DueAsync(state, Now.AddDays(-15));
        var recent = await DueAsync(state, Now.AddDays(-13));
        await RunAsync("DueActions");
        Assert.False(await DueExistsAsync(old));
        Assert.True(await DueExistsAsync(recent));
    }

    [IntegrationFact]
    public async Task Parked_due_actions_are_purged_after_90_days()
    {
        var old = await DueAsync("Failed", Now.AddDays(-91));
        var middle = await DueAsync("Failed", Now.AddDays(-30));
        await RunAsync("DueActions");
        Assert.False(await DueExistsAsync(old));
        Assert.True(await DueExistsAsync(middle));
    }
}

// Runs without a database: checks the statements the rules send.
public class TaxiRetentionRuleSqlTests
{
    private static RetentionRuleDefinition Rule(string name) => TaxiRequestRetentionRules.Definitions.Single(d => d.Name == name);

    [Fact]
    public void Two_rules_run_due_actions_first_in_the_trip_enquiries_slot()
    {
        Assert.Equal(["DueActions", "TaxiRequests"], TaxiRequestRetentionRules.Definitions.Select(d => d.Name));
        Assert.All(TaxiRequestRetentionRules.Definitions, d => Assert.Equal(RetentionGroups.TripEnquiries, d.Group));
        Assert.Equal([14, 90], TaxiRequestRetentionRules.Definitions.Select(d => d.DefaultDays));
    }

    [Fact]
    public void Request_rule_names_only_final_states_and_needs_a_close_time()
    {
        var sql = Rule("TaxiRequests").EligibleSql;
        foreach (var active in new[] { "Open", "Collecting", "Selected", "Booked", "InProgress" })
            Assert.DoesNotContain($"'{active}'", sql);
        foreach (var final in new[] { "Cancelled", "Expired", "Completed" })
            Assert.Contains($"'{final}'", sql);
        Assert.Contains("\"ClosedAt\" < @cutoff", sql);
    }

    [Fact]
    public void Due_action_rule_never_names_pending_or_running_and_keeps_failed_longer()
    {
        var sql = Rule("DueActions").EligibleSql;
        Assert.DoesNotContain("'Pending'", sql);
        Assert.DoesNotContain("'Running'", sql);
        Assert.Contains("'Done','Cancelled','Skipped'", sql);
        Assert.Contains("'Failed' AND t.\"DoneAt\" < @cutoff AND t.\"DoneAt\" < @now - interval '90 days'", sql);
    }

    [Fact]
    public void Request_rule_purges_exactly_the_final_states()
    {
        var sql = Rule("TaxiRequests").EligibleSql;
        Assert.All(RequestStates.Final, s => Assert.Contains($"'{s}'", sql));
        Assert.All(RequestStates.Active, s => Assert.DoesNotContain($"'{s}'", sql));
        Assert.Equal(RequestStates.All.OrderBy(x => x), RequestStates.Active.Concat(RequestStates.Final).OrderBy(x => x));
    }

    [Fact]
    public void Due_action_rule_purges_every_final_state_and_no_other()
    {
        var sql = Rule("DueActions").EligibleSql;
        Assert.All(DueActionStates.Final, s => Assert.Contains($"'{s}'", sql));
        Assert.All(DueActionStates.All.Except(DueActionStates.Final), s => Assert.DoesNotContain($"'{s}'", sql));
        Assert.Equal(DueActionStates.Final.Where(s => s != DueActionStates.Failed), TaxiRequestRetentionRules.QuickPurgeDueActionStates);
    }

    [Fact]
    public void Due_actions_is_a_guard_exempt_queue_and_requests_keep_the_guard()
    {
        Assert.True(Rule("DueActions").GuardExempt);
        Assert.False(Rule("TaxiRequests").GuardExempt);
    }

    [Fact]
    public void Both_rules_wrap_their_or_and_use_known_parameters()
    {
        foreach (var d in TaxiRequestRetentionRules.Definitions)
        {
            var rule = new DeclarativeRetentionRule(d);
            Assert.Contains("(" + d.EligibleSql + ")", rule.BuildDeleteSql());
            Assert.Contains("SKIP LOCKED", rule.BuildDeleteSql());
            foreach (Match m in Regex.Matches(d.EligibleSql, @"@\w+"))
                Assert.Contains(m.Value, RetentionParams.Allowed);
        }
    }

    [Fact]
    public void Bookings_and_ratings_are_never_named_by_any_rule()
    {
        var rules = RetentionRuleRegistration.Discover(typeof(RetentionRunner).Assembly).OfType<DeclarativeRetentionRule>().ToList();
        Assert.DoesNotContain(rules, r => r.Definition.Table is "TaxiBookings" or "TaxiRatings");
        Assert.DoesNotContain(rules, r => r.BuildDeleteSql().Contains("TaxiBookings") || r.BuildDeleteSql().Contains("TaxiRatings"));
    }
}
