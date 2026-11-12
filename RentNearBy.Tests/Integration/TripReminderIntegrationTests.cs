using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;

namespace RentNearBy.Tests.Integration;

// Written for PostgreSQL (RENTNEARBY_TEST_DB); skipped without it. These cover the SQL stop cases and claims that the pure planner
// tests cannot: the predicates, the claim-with-the-outbox-row rule and restart safety.
public class TripReminderIntegrationTests(TestDatabase db) : IntegrationTestBase(db), IAsyncLifetime
{
    // A real approved stay the shortlist rows point at: the reminder queries need the listing to exist and be bookable.
    private readonly Journeys.JourneyHarness _harness = new(db);
    private Guid _liveStay;

    public async ValueTask InitializeAsync()
        => _liveStay = await _harness.AddStayAsync(await _harness.AddStayOwnerAsync("Live host"));

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    // 07:00 IST on the run day: after the 05:00 morning time.
    private static readonly DateTime RunAt = TripReminderRules.MorningDueUtc(IstTime.TodayIst(DateTime.UtcNow)).AddHours(2);
    private static readonly DateOnly Today = IstTime.TodayIst(RunAt);

    private static async Task<Guid> UserAsync(ApplicationDbContext ctx, string name)
    {
        var id = Guid.NewGuid();
        ctx.Users.Add(new User
        {
            Id = id, PhoneNumber = "9" + Random.Shared.NextInt64(100_000_000, 999_999_999), Name = name, IsActive = true, IsPhoneVerified = true,
            RoleId = UserRoles.SeekerId, CreatedAt = DateTime.UtcNow.AddDays(-30), UpdatedAt = DateTime.UtcNow.AddDays(-30),
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> TripAsync(ApplicationDbContext ctx, Guid owner, DateOnly? start = null, DateTime? mutedAt = null, string status = TripStatuses.Planned)
    {
        var id = Guid.NewGuid();
        ctx.Trips.Add(new Trip
        {
            Id = id, OwnerUserId = owner, CityKey = 210000001, CityName = "Nainital", Title = "Nainital", Status = status,
            PlanJson = "{}", StartDate = start ?? Today.AddDays(30), MutedAt = mutedAt, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private TripShortlistItem Item(Guid trip, Guid user, string state = "Active", string? reason = null, DateTime? addedAt = null) => new()
    {
        Id = Guid.NewGuid(), TripId = trip, TravellerUserId = user, StepKey = "stay:1", Kind = "Stay", ListingId = _liveStay,
        ListingName = "Hill View", AddedAt = addedAt ?? RunAt.AddDays(-1), ClosesAt = (addedAt ?? RunAt.AddDays(-1)).AddHours(72),
        State = state, ClosedReason = reason, ClosedAt = state == "Closed" ? RunAt.AddHours(-3) : null,
    };

    private static TripEnquiry Enquiry(Guid trip, Guid traveller, Guid owner, string status = "Asked") => new()
    {
        Id = Guid.NewGuid(), TripId = trip, TravellerUserId = traveller, OwnerUserId = owner, Kind = "Stay", ListingId = Guid.NewGuid(),
        ListingName = "Hill View", Status = status, StepKey = "stay:1", ExpiresAt = RunAt.AddDays(1), CreatedAt = RunAt.AddDays(-1), UpdatedAt = RunAt.AddDays(-1),
    };

    private async Task<List<string>> OutboxKeysAsync(Guid user)
    {
        await using var ctx = CreateContext();
        var n = user.ToString("N");
        return await ctx.OutboxMessages.AsNoTracking().Where(o => o.Key.Contains(n)).Select(o => o.Key).ToListAsync();
    }

    private Task<int> RunAsync() => RunAsync(RunAt);

    private async Task<int> RunAsync(DateTime at)
    {
        await using var ctx = CreateContext();
        return await new TripReminderRunner(NullLogger.Instance).RunAsync(ctx, at, CancellationToken.None);
    }

    [IntegrationFact]
    public async Task Active_shortlist_gets_one_morning_push_and_a_second_run_sends_nothing_more()
    {
        Guid user, item;
        await using (var ctx = CreateContext())
        {
            user = await UserAsync(ctx, "Asha");
            var trip = await TripAsync(ctx, user);
            var row = Item(trip, user);
            item = row.Id;
            ctx.TripShortlistItems.Add(row);
            await ctx.SaveChangesAsync();
        }

        await RunAsync();
        await RunAsync();

        Assert.Equal([TripReminderRules.TravellerKey(user, Today)], await OutboxKeysAsync(user));
        await using var check = CreateContext();
        Assert.Equal(Today, (await check.TripShortlistItems.AsNoTracking().SingleAsync(i => i.Id == item)).RemindedOn);
    }

    [IntegrationFact]
    public async Task Shortlist_added_after_the_morning_time_waits_for_the_next_morning()
    {
        Guid user;
        await using (var ctx = CreateContext())
        {
            user = await UserAsync(ctx, "Asha");
            var trip = await TripAsync(ctx, user);
            ctx.TripShortlistItems.Add(Item(trip, user, addedAt: RunAt.AddMinutes(-10)));
            await ctx.SaveChangesAsync();
        }
        await RunAsync();
        Assert.Empty(await OutboxKeysAsync(user));
    }

    [IntegrationFact]
    public async Task Closed_no_reply_step_gets_exactly_one_final_push_then_never_again()
    {
        Guid user, item;
        await using (var ctx = CreateContext())
        {
            user = await UserAsync(ctx, "Asha");
            var trip = await TripAsync(ctx, user);
            var row = Item(trip, user, "Closed", "NoReply", RunAt.AddDays(-4));
            item = row.Id;
            ctx.TripShortlistItems.Add(row);
            await ctx.SaveChangesAsync();
        }

        await RunAsync();
        Assert.Single(await OutboxKeysAsync(user));
        await using (var check = CreateContext())
            Assert.NotNull((await check.TripShortlistItems.AsNoTracking().SingleAsync(i => i.Id == item)).FinalPushedAt);

        await RunAsync(RunAt.AddDays(1));
        Assert.Single(await OutboxKeysAsync(user));
    }

    [IntegrationTheory]
    [InlineData("muted")]
    [InlineData("completed")]
    [InlineData("started")]
    [InlineData("removed")]
    [InlineData("booked")]
    public async Task Stop_cases_send_nothing(string stop)
    {
        Guid user;
        await using (var ctx = CreateContext())
        {
            user = await UserAsync(ctx, "Asha");
            var trip = await TripAsync(ctx, user,
                start: stop == "started" ? Today : null,
                mutedAt: stop == "muted" ? DateTime.UtcNow : null,
                status: stop == "completed" ? TripStatuses.Completed : TripStatuses.Planned);
            ctx.TripShortlistItems.Add(stop switch
            {
                "removed" => Item(trip, user, "Closed", "Removed"),
                "booked" => Item(trip, user, "Booked", "Booked"),
                _ => Item(trip, user),
            });
            await ctx.SaveChangesAsync();
        }
        await RunAsync();
        Assert.Empty(await OutboxKeysAsync(user));
    }

    [IntegrationTheory]
    [InlineData("deleted")]
    [InlineData("notApproved")]
    [InlineData("notAccepting")]
    public async Task Shortlist_of_a_listing_that_is_gone_or_off_gets_no_reminder(string reason)
    {
        Guid user;
        await using (var ctx = CreateContext())
        {
            user = await UserAsync(ctx, "Asha");
            var trip = await TripAsync(ctx, user);
            ctx.TripShortlistItems.Add(Item(trip, user));
            ctx.TripShortlistItems.Add(Item(trip, user, "Closed", "NoReply", RunAt.AddDays(-4)));
            await ctx.SaveChangesAsync();
            var id = _liveStay;
            if (reason == "deleted") await ctx.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "StayListings" SET "IsDeleted" = true WHERE "Id" = {id}""");
            else if (reason == "notApproved") await ctx.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "StayListings" SET "Status" = 'Rejected' WHERE "Id" = {id}""");
            else await ctx.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "StayListings" SET "AcceptingGuests" = false WHERE "Id" = {id}""");
        }
        await RunAsync();
        Assert.Empty(await OutboxKeysAsync(user));
    }

    [IntegrationFact]
    public async Task Closing_the_listing_removes_its_active_shortlist_rows()
    {
        Guid user, item;
        await using (var ctx = CreateContext())
        {
            user = await UserAsync(ctx, "Asha");
            var trip = await TripAsync(ctx, user);
            var row = Item(trip, user);
            item = row.Id;
            ctx.TripShortlistItems.Add(row);
            await ctx.SaveChangesAsync();
        }
        await using (var scope = _harness.NewScope())
        {
            await scope.Closure.OnListingsRemovedAsync(ListingKinds.Stay, [_liveStay]);
        }
        await using var check = CreateContext();
        var closed = await check.TripShortlistItems.AsNoTracking().SingleAsync(i => i.Id == item);
        Assert.Equal(("Closed", "Unavailable"), (closed.State, closed.ClosedReason));
    }

    [IntegrationFact]
    public async Task Host_is_still_reminded_when_the_traveller_muted_the_trip()
    {
        Guid host, traveller;
        await using (var ctx = CreateContext())
        {
            traveller = await UserAsync(ctx, "Asha");
            host = await UserAsync(ctx, "Host");
            var trip = await TripAsync(ctx, traveller, mutedAt: DateTime.UtcNow);
            ctx.TripEnquiries.Add(Enquiry(trip, traveller, host));
            await ctx.SaveChangesAsync();
        }
        await RunAsync();
        Assert.Equal([TripReminderRules.HostKey(host, Today)], await OutboxKeysAsync(host));
    }

    [IntegrationFact]
    public async Task Host_with_an_unanswered_enquiry_is_reminded_once_a_day_and_not_after_answering()
    {
        Guid host, traveller, enquiryId;
        await using (var ctx = CreateContext())
        {
            traveller = await UserAsync(ctx, "Asha");
            host = await UserAsync(ctx, "Host");
            var trip = await TripAsync(ctx, traveller);
            var e = Enquiry(trip, traveller, host);
            enquiryId = e.Id;
            ctx.TripEnquiries.Add(e);
            await ctx.SaveChangesAsync();
        }

        await RunAsync();
        await RunAsync();
        Assert.Equal([TripReminderRules.HostKey(host, Today)], await OutboxKeysAsync(host));

        // Next morning the host answered (Offered): no second reminder.
        await using (var ctx = CreateContext())
            await ctx.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "TripEnquiries" SET "Status" = 'Offered' WHERE "Id" = {enquiryId}""");
        await RunAsync(RunAt.AddDays(1));
        Assert.Single(await OutboxKeysAsync(host));
    }

    [IntegrationFact]
    public async Task Person_who_is_both_traveller_and_host_gets_one_push_per_role()
    {
        Guid both, other;
        await using (var ctx = CreateContext())
        {
            both = await UserAsync(ctx, "Both");
            other = await UserAsync(ctx, "Other");
            var tripA = await TripAsync(ctx, both);
            var tripB = await TripAsync(ctx, other);
            ctx.TripShortlistItems.Add(Item(tripA, both));
            ctx.TripEnquiries.Add(Enquiry(tripB, other, both));
            await ctx.SaveChangesAsync();
        }
        await RunAsync();
        Assert.Equal(2, (await OutboxKeysAsync(both)).Count);
    }

    // B3 gaps (run on PostgreSQL; the same predicates were also run against a scratch PostgreSQL 18.3 schema without PostGIS).
    [IntegrationFact]
    public async Task Host_is_reminded_only_after_twelve_hours_and_also_for_a_trip_less_enquiry()
    {
        Guid hostYoung, hostOld, hostTripLess;
        await using (var ctx = CreateContext())
        {
            var t = await UserAsync(ctx, "Asha");
            hostYoung = await UserAsync(ctx, "Young");
            hostOld = await UserAsync(ctx, "Old");
            hostTripLess = await UserAsync(ctx, "TripLess");
            var trip = await TripAsync(ctx, t);
            var young = Enquiry(trip, t, hostYoung);
            young.CreatedAt = RunAt.AddHours(-6);
            var old = Enquiry(trip, t, hostOld);
            old.CreatedAt = RunAt.AddHours(-20);
            old.StepKey = "stay:2";
            var tripLess = Enquiry(trip, t, hostTripLess);
            tripLess.TripId = null;
            tripLess.StepKey = null;
            ctx.TripEnquiries.AddRange(young, old, tripLess);
            await ctx.SaveChangesAsync();
        }
        await RunAsync();
        Assert.Empty(await OutboxKeysAsync(hostYoung));
        Assert.Single(await OutboxKeysAsync(hostOld));
        Assert.Single(await OutboxKeysAsync(hostTripLess));
    }

    [IntegrationFact]
    public async Task Shortlist_of_a_step_with_an_open_enquiry_is_not_nagged_and_an_old_final_row_is_not_pushed()
    {
        Guid openUser, oldUser;
        await using (var ctx = CreateContext())
        {
            openUser = await UserAsync(ctx, "Open");
            oldUser = await UserAsync(ctx, "Old");
            var trip = await TripAsync(ctx, openUser);
            var trip2 = await TripAsync(ctx, oldUser);
            ctx.TripShortlistItems.Add(Item(trip, openUser));
            var open = Enquiry(trip, openUser, await UserAsync(ctx, "Host"));
            open.CreatedAt = RunAt.AddHours(-1);
            ctx.TripEnquiries.Add(open);
            var old = Item(trip2, oldUser, "Closed", "NoReply", RunAt.AddDays(-5));
            old.ClosedAt = RunAt.AddDays(-4);
            ctx.TripShortlistItems.Add(old);
            await ctx.SaveChangesAsync();
        }
        await RunAsync();
        Assert.Empty(await OutboxKeysAsync(openUser));
        Assert.Empty(await OutboxKeysAsync(oldUser));
    }

    [IntegrationFact]
    public async Task Expired_step_less_enquiry_gets_one_generic_final_push()
    {
        Guid user;
        await using (var ctx = CreateContext())
        {
            user = await UserAsync(ctx, "Asha");
            var trip = await TripAsync(ctx, user);
            var e = Enquiry(trip, user, await UserAsync(ctx, "Host"), "Expired");
            e.StepKey = null;
            e.CreatedAt = RunAt.AddHours(-80);
            e.ExpiresAt = e.CreatedAt.AddHours(TripExecutionRules.EnquiryHours);
            e.UpdatedAt = RunAt.AddHours(-3);
            ctx.TripEnquiries.Add(e);
            await ctx.SaveChangesAsync();
        }
        await RunAsync();
        await RunAsync(RunAt.AddDays(1));
        Assert.Single(await OutboxKeysAsync(user));
    }

    // Opt-in: RNB_REMINDER_LOAD_ROWS=200000 seeds that many shortlist rows (one user and trip each) and samples process RSS
    // while the run goes through them. Constant memory means the peak stays near the baseline, not near the row count.
    [IntegrationFact]
    public async Task Large_run_stays_in_constant_memory()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("RNB_REMINDER_LOAD_ROWS"), out var rows) || rows <= 0) return;

        for (var from = 0; from < rows; from += 5000)
        {
            await using var ctx = CreateContext();
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            var take = Math.Min(5000, rows - from);
            var users = Enumerable.Range(0, take).Select(_ => new User
            {
                Id = Guid.NewGuid(), PhoneNumber = "8" + Random.Shared.NextInt64(100_000_000, 999_999_999), Name = "Load", IsActive = true,
                IsPhoneVerified = true, RoleId = UserRoles.SeekerId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            }).ToList();
            var trips = users.Select(u => new Trip
            {
                Id = Guid.NewGuid(), OwnerUserId = u.Id, CityKey = 210000001, CityName = "Nainital", Title = "Nainital", Status = TripStatuses.Planned,
                PlanJson = "{}", StartDate = Today.AddDays(30), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            }).ToList();
            ctx.Users.AddRange(users);
            ctx.Trips.AddRange(trips);
            ctx.TripShortlistItems.AddRange(trips.Select(t => Item(t.Id, t.OwnerUserId)));
            await ctx.SaveChangesAsync();
        }

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var baseline = process.WorkingSet64;
        var peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                await Task.Delay(100);
            }
        });

        var watch = Stopwatch.StartNew();
        var sent = await RunAsync();
        watch.Stop();
        await stop.CancelAsync();
        await sampler;

        Assert.True(sent >= rows, $"sent {sent} of {rows}");
        Assert.True(peak - baseline < 400L * 1024 * 1024, $"peak grew {(peak - baseline) / (1024 * 1024)} MB in {watch.Elapsed}");
    }
}
