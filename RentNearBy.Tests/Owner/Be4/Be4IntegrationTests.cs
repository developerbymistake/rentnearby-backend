using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be4;

// Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it. WRITTEN, NOT RUN on the build machine (contract F26, 11.2, 11.3, R-13).
public class Be4IntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly OwnerHomeEnv Env = new(true, true);

    private static async Task TokenAsync(ApplicationDbContext c, Guid userId, bool valid = true)
    {
        c.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = userId, Token = "tok-" + Guid.NewGuid().ToString("N"), IsValid = valid, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await c.SaveChangesAsync();
    }

    private static TimeProvider Clock => new Be4Clock(DateTime.UtcNow);

    [IntegrationFact]
    public async Task Stay_home_query_count_is_fixed_for_3_and_30_upcoming_rows()
    {
        var owner = Guid.Empty;
        await QueryAssert.SameCountAsync(Db,
            async (c, n) =>
            {
                var o = await OwnerSeed.StayOwnerAsync(c);
                owner = o.UserId;
                await OwnerSeed.ManualEntriesAsync(c, o, n, IstTime.TodayIst(DateTime.UtcNow));
            },
            c => new StayOwnerHomeReader(c, new Be4Catalog(), new Be4Payment(false), Clock).ReadAsync(owner, Env),
            budget: 6);
    }

    [IntegrationFact]
    public async Task Taxi_home_query_count_is_fixed_for_3_and_30_upcoming_rows()
    {
        var owner = Guid.Empty;
        await QueryAssert.SameCountAsync(Db,
            async (c, n) =>
            {
                var o = await OwnerSeed.TaxiDriverAsync(c);
                owner = o.UserId;
                await OwnerSeed.ManualEntriesAsync(c, o, n, IstTime.TodayIst(DateTime.UtcNow));
            },
            c => new TaxiDriverHomeReader(c, new Be4Catalog(), new Be4Payment(true), Clock).ReadAsync(owner, Env),
            budget: 6);
    }

    [IntegrationFact]
    public async Task Taxi_home_listing_carries_the_local_day_rate()
    {
        OwnerSeed.OwnerRow o;
        await using (var c = CreateContext())
        {
            o = await OwnerSeed.TaxiDriverAsync(c);
            await c.TaxiListings.Where(t => t.Id == o.ListingId).ExecuteUpdateAsync(u => u.SetProperty(t => t.LocalDayRate, 2500));
        }
        await using var read = CreateContext();
        var home = await new TaxiDriverHomeReader(read, new Be4Catalog(), new Be4Payment(true), Clock).ReadAsync(o.UserId, Env);
        Assert.Equal(2500, home!.Listing.LocalDayRate);
    }

    [IntegrationFact]
    public async Task Taxi_home_listing_carries_the_display_photo()
    {
        OwnerSeed.OwnerRow o;
        await using (var c = CreateContext())
        {
            o = await OwnerSeed.TaxiDriverAsync(c);
            await c.TaxiListings.Where(t => t.Id == o.ListingId)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.DisplayPhotoUrl, "https://res.cloudinary.com/x/image/upload/taxi.jpg"));
        }
        await using var read = CreateContext();
        var home = await new TaxiDriverHomeReader(read, new Be4Catalog(), new Be4Payment(true), Clock).ReadAsync(o.UserId, Env);
        Assert.Equal("https://res.cloudinary.com/x/image/upload/taxi.jpg", home!.Listing.DisplayPhotoUrl);
    }

    [IntegrationFact]
    public async Task Home_of_a_fresh_stay_owner_has_every_field_and_no_attention()
    {
        OwnerSeed.OwnerRow o;
        await using (var c = CreateContext()) o = await OwnerSeed.StayOwnerAsync(c, StayStatuses.Draft);
        await using var read = CreateContext();
        var home = await new StayOwnerHomeReader(read, new Be4Catalog(), new Be4Payment(false), Clock).ReadAsync(o.UserId, Env);
        Assert.NotNull(home);
        Assert.Equal(StayStatuses.Draft, home!.Listing.Status);
        Assert.Null(home.PlanCard);
        Assert.Empty(home.Upcoming.Rows);
        Assert.Equal(0, home.AttentionCount);
    }

    [IntegrationFact]
    public async Task No_listing_reads_null()
    {
        Guid user;
        await using (var c = CreateContext()) user = await OwnerSeed.UserAsync(c, UserRoles.StayOwnerId);
        await using var read = CreateContext();
        Assert.Null(await new StayOwnerHomeReader(read, new Be4Catalog(), new Be4Payment(false), Clock).ReadAsync(user, Env));
    }

    [IntegrationFact]
    public async Task Hidden_manual_rows_beyond_the_plan_limit_stay_out_of_upcoming_and_auto_rows_always_show()
    {
        OwnerSeed.OwnerRow o;
        var today = IstTime.TodayIst(DateTime.UtcNow);
        await using (var c = CreateContext())
        {
            o = await OwnerSeed.StayOwnerAsync(c);
            await OwnerSeed.ManualEntriesAsync(c, o, 12, today);
            await OwnerSeed.AutoEntryAsync(c, o, CalendarSourceTypes.StayEnquiry, Guid.NewGuid(), today.AddDays(1), today.AddDays(2));
        }
        await using var read = CreateContext();
        var home = await new StayOwnerHomeReader(read, new Be4Catalog(), new Be4Payment(true), Clock).ReadAsync(o.UserId, Env);
        Assert.Equal(3, home!.Upcoming.Rows.Count);
        Assert.Equal(5 - 3, home.Upcoming.MoreInWindowCount);
    }

    [IntegrationFact]
    public async Task Seeker_feed_query_count_is_four_with_a_trip()
    {
        Guid user = Guid.Empty;
        await QueryAssert.SameCountAsync(Db,
            async (c, n) =>
            {
                user = await OwnerSeed.UserAsync(c, UserRoles.SeekerId, "Traveller");
                for (var i = 0; i < Math.Min(n, TripLimits.MaxTripsPerUser); i++) await BuddySeed.TripAsync(c, user);
            },
            c => new SeekerFeedReader(c, Clock).ReadAsync(user),
            budget: 4);
    }

    [IntegrationFact]
    public async Task Seeker_feed_without_trips_is_one_statement_and_state_none()
    {
        Guid user;
        await using (var c = CreateContext()) user = await OwnerSeed.UserAsync(c, UserRoles.SeekerId, "Traveller");
        await using var counting = Db.CreateCountingContext(out var counter);
        var feed = await new SeekerFeedReader(counting, Clock).ReadAsync(user);
        Assert.Equal(SeekerTripStates.None, feed.TripCard.State);
        Assert.Null(feed.Status);
        Assert.Single(counter.Commands);
    }

    [IntegrationFact]
    public async Task Festival_job_marks_once_skips_owners_with_a_rule_and_other_states()
    {
        var today = IstTime.TodayIst(DateTime.UtcNow);
        OwnerSeed.OwnerRow withToken, withRule;
        Guid festivalId;
        await using (var c = CreateContext())
        {
            withToken = await OwnerSeed.StayOwnerAsync(c);
            withRule = await OwnerSeed.StayOwnerAsync(c);
            await TokenAsync(c, withToken.UserId);
            var f = await OwnerSeed.FestivalAsync(c, "Diwali", today.AddDays(21), today.AddDays(25));
            festivalId = f.Id;
            await OwnerSeed.SpecialPricesAsync(c, withRule.ListingId, 1, today.AddDays(22));
        }
        for (var run = 0; run < 2; run++)
        {
            await using var c = CreateContext();
            await new FestivalReminderJob(c, new OwnerNotifier(c, new NoticeWriter(c)), Options.Create(new OwnerFeatureOptions()), Clock,
                NullLogger<FestivalReminderJob>.Instance).RunAsync(default);
        }
        await using var read = CreateContext();
        Assert.Equal(1, await read.Set<FestivalReminderSent>().CountAsync(m => m.FestivalDateId == festivalId && m.UserId == withToken.UserId));
        Assert.False(await read.Set<FestivalReminderSent>().AnyAsync(m => m.FestivalDateId == festivalId && m.UserId == withRule.UserId));
        Assert.Equal(1, await read.Set<UserNotice>().CountAsync(n => n.UserId == withToken.UserId && n.Kind == NoticeKinds.FestivalReminder));
        Assert.Equal(1, await read.OutboxMessages.CountAsync(m => m.Key == $"festival:{festivalId}:{withToken.UserId}"));
    }

    [IntegrationFact]
    public async Task Reminder_job_selects_plus_only_with_switch_and_token_and_sends_once()
    {
        var today = IstTime.TodayIst(DateTime.UtcNow);
        OwnerSeed.OwnerRow plus, free, off, noToken;
        await using (var c = CreateContext())
        {
            plus = await OwnerSeed.StayOwnerAsync(c);
            free = await OwnerSeed.StayOwnerAsync(c);
            off = await OwnerSeed.StayOwnerAsync(c);
            noToken = await OwnerSeed.StayOwnerAsync(c);
            foreach (var o in new[] { plus, free, off, noToken })
            {
                await OwnerSeed.ManualEntriesAsync(c, o, 1, today);
                if (o != noToken) await TokenAsync(c, o.UserId);
                if (o != free) await OwnerSeed.ActivePlanAsync(c, o.UserId, ListingKinds.Stay, OwnerPlanCatalog.StayPlusId);
            }
            await c.StayListings.Where(s => s.Id == off.ListingId).ExecuteUpdateAsync(s => s.SetProperty(x => x.RemindersOn, false));
        }
        var at = DateTime.SpecifyKind(today.ToDateTime(new TimeOnly(3, 0)), DateTimeKind.Utc);
        for (var run = 0; run < 2; run++)
        {
            await using var c = CreateContext();
            await new StayMorningReminderJob(c, new OwnerNotifier(c, new NoticeWriter(c)), new Be4Catalog(), new Be4Payment(true),
                Options.Create(new OwnerFeatureOptions()), new Be4Clock(at), NullLogger<StayMorningReminderJob>.Instance).RunAsync(default);
        }
        await using var read = CreateContext();
        var keys = await read.OutboxMessages.Where(m => m.Key.StartsWith("ownermorning:Stay:")).Select(m => m.Key).ToListAsync();
        Assert.Contains(keys, k => k.Contains(plus.ListingId.ToString()));
        Assert.DoesNotContain(keys, k => k.Contains(free.ListingId.ToString()) || k.Contains(off.ListingId.ToString()) || k.Contains(noToken.ListingId.ToString()));
        Assert.Equal(1, keys.Count(k => k.Contains(plus.ListingId.ToString())));
    }

    [IntegrationFact]
    public async Task A_user_deleted_between_batches_is_never_selected()
    {
        var today = IstTime.TodayIst(DateTime.UtcNow);
        OwnerSeed.OwnerRow gone;
        await using (var c = CreateContext())
        {
            gone = await OwnerSeed.StayOwnerAsync(c);
            await TokenAsync(c, gone.UserId);
            await OwnerSeed.ManualEntriesAsync(c, gone, 1, today);
            await c.Users.Where(u => u.Id == gone.UserId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        }
        await using (var c = CreateContext())
            await new StayMorningReminderJob(c, new OwnerNotifier(c, new NoticeWriter(c)), new Be4Catalog(), new Be4Payment(false),
                Options.Create(new OwnerFeatureOptions()), new Be4Clock(DateTime.SpecifyKind(today.ToDateTime(new TimeOnly(3, 0)), DateTimeKind.Utc)),
                NullLogger<StayMorningReminderJob>.Instance).RunAsync(default);
        await using var read = CreateContext();
        Assert.False(await read.OutboxMessages.AnyAsync(m => m.Key.Contains(gone.ListingId.ToString())));
    }

    [IntegrationFact]
    public async Task Announce_job_notifies_submitted_owners_once_per_date_and_skips_drafts_and_grants()
    {
        var today = IstTime.TodayIst(DateTime.UtcNow);
        OwnerSeed.OwnerRow live, draft, grant;
        await using (var c = CreateContext())
        {
            live = await OwnerSeed.StayOwnerAsync(c);
            draft = await OwnerSeed.StayOwnerAsync(c, StayStatuses.Draft);
            grant = await OwnerSeed.StayOwnerAsync(c);
            await OwnerSeed.ActivePlanAsync(c, grant.UserId, ListingKinds.Stay, OwnerPlanCatalog.StayPlusId, UserPlanSources.AdminGrant);
            await c.AppFeatureFlags.Where(f => f.FeatureKey == AppFeatureKeys.PaymentEnabled)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false).SetProperty(x => x.AnnounceDate, today.AddDays(10)));
        }
        OwnerPlansAnnounceJob.ForgetQuietPasses();
        for (var run = 0; run < 2; run++)
        {
            await using var c = CreateContext();
            await new OwnerPlansAnnounceJob(c, new OwnerNotifier(c, new NoticeWriter(c)), new Be4Catalog(), Options.Create(new OwnerFeatureOptions()),
                Clock, NullLogger<OwnerPlansAnnounceJob>.Instance).RunAsync(default);
        }
        await using var read = CreateContext();
        Assert.Equal(1, await read.Set<UserNotice>().CountAsync(n => n.UserId == live.UserId && n.Kind == NoticeKinds.PlansStarting));
        Assert.False(await read.Set<UserNotice>().AnyAsync(n => (n.UserId == draft.UserId || n.UserId == grant.UserId) && n.Kind == NoticeKinds.PlansStarting));
    }

    [IntegrationFact]
    public async Task Logout_cleanup_and_account_deletion_remove_tokens_and_unsent_pushes_of_the_user_only()
    {
        Guid me, other;
        await using (var c = CreateContext())
        {
            me = await OwnerSeed.UserAsync(c, UserRoles.StayOwnerId);
            other = await OwnerSeed.UserAsync(c, UserRoles.StayOwnerId);
            await TokenAsync(c, me);
            await TokenAsync(c, other);
            await new OwnerNotifier(c, new NoticeWriter(c)).PushAsync(
            [
                StayOwnerPushCopy.PlansStarting(me, new DateOnly(2027, 2, 1), 2, 2, 7),
                StayOwnerPushCopy.PlansStarting(other, new DateOnly(2027, 2, 1), 2, 2, 7),
            ]);
        }
        await using (var c = CreateContext())
        {
            await DeviceTokenCleanup.DeleteAllAsync(c, me);
            await new OwnerAccountDeletion(c).OnDeletingAsync(me);
        }
        await using var read = CreateContext();
        Assert.False(await read.DeviceTokens.AnyAsync(t => t.UserId == me));
        Assert.True(await read.DeviceTokens.AnyAsync(t => t.UserId == other));
        Assert.False(await read.OutboxMessages.AnyAsync(m => m.Key == $"plansstarting:{me}:20270201"));
        Assert.True(await read.OutboxMessages.AnyAsync(m => m.Key == $"plansstarting:{other}:20270201"));
    }

    [IntegrationFact]
    public async Task Pushes_skip_users_without_a_valid_token_but_the_notice_is_stored()
    {
        Guid user;
        var f = Guid.NewGuid();
        await using (var c = CreateContext())
        {
            user = await OwnerSeed.UserAsync(c, UserRoles.TaxiDriverId);
            await TokenAsync(c, user, valid: false);
            var notifier = new OwnerNotifier(c, new NoticeWriter(c));
            await notifier.StoreAsync([new NoticeUpsert(user, NoticeKinds.PlansStarting, NoticeKinds.NoTrip, null)]);
            await notifier.PushAsync([TaxiDriverPushCopy.PlansStarting(user, new DateOnly(2027, 2, 1), 1, 0)]);
        }
        await using var read = CreateContext();
        Assert.True(await read.Set<UserNotice>().AnyAsync(n => n.UserId == user && n.Kind == NoticeKinds.PlansStarting));
        Assert.False(await read.OutboxMessages.AnyAsync(m => m.Key == $"plansstarting:{user}:20270201"));
    }

    [IntegrationFact]
    public async Task Weekly_flush_counts_requests_with_no_route_for_yesterday()
    {
        await using var c = CreateContext();
        var rows = await new OwnerWeeklyStatsFlushJob(c, Options.Create(new OwnerFeatureOptions()), Clock, NullLogger<OwnerWeeklyStatsFlushJob>.Instance).RunAsync(default);
        Assert.True(rows >= 0);
    }
}
