using System.Text.Json;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Social;

// 72-hour post lock + delete-alone rule (Buddy_resume/post_notify/SPEC.md use cases 39-44): pure rules, error table, wiring pins.
public class PostLockRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = IstTime.TodayIst(Now);

    private static PostLockState Posted(TimeSpan ago, bool isPublic = true, string status = TripStatuses.Draft, DateOnly? start = null,
        DateOnly? end = null, short? window = 2, DateTime? lastPublished = null, bool everPublished = true)
        => new(isPublic, everPublished ? Now - ago : null, everPublished ? lastPublished ?? Now - ago : null, status, start, end, window);

    private static JsonElement Data(PostLockRefusal r) => JsonSerializer.SerializeToElement(r.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void The_lock_is_72_hours_and_listed_with_the_caps()
    {
        Assert.Equal(72, SocialLimits.PostLockHours);
        Assert.Equal(TimeSpan.FromHours(72), PostLockRules.Lock);
        Assert.Equal((409, 409), (SocialErrorTypes.StatusOf(SocialErrorTypes.PostLocked), SocialErrorTypes.StatusOf(SocialErrorTypes.RemoveMembersFirst)));
        Assert.Contains(SocialErrorTypes.PostLocked, SocialErrorTypes.All);
        Assert.Contains(SocialErrorTypes.RemoveMembersFirst, SocialErrorTypes.All);
        Assert.False(SocialErrorTypes.IsCap(SocialErrorTypes.PostLocked));        // not a plan cap: no Plus prompt
    }

    [Fact]
    public void Boundary_71_59_is_locked_and_72_00_is_free()
    {
        var locked = Posted(TimeSpan.FromHours(72) - TimeSpan.FromMinutes(1));
        Assert.Equal(Now + TimeSpan.FromMinutes(1), PostLockRules.UnlocksAt(locked, Now));
        Assert.Null(PostLockRules.UnlocksAt(Posted(TimeSpan.FromHours(72)), Now));
        Assert.Equal(DateTimeKind.Utc, PostLockRules.UnlocksAt(locked, Now)!.Value.Kind);
    }

    [Fact]
    public void The_window_starts_at_the_latest_publish_and_falls_back_to_the_first()
    {
        // Bump protection kept PublishedAt 5 days back; the re-publish 1 hour ago starts a new 72 hours.
        var republished = Posted(TimeSpan.FromDays(5), lastPublished: Now.AddHours(-1));
        Assert.Equal(Now.AddHours(71), PostLockRules.UnlocksAt(republished, Now));
        var oldRow = new PostLockState(true, Now.AddHours(-10), null, TripStatuses.Draft, null, null, 2);
        Assert.Equal(Now.AddHours(62), PostLockRules.UnlocksAt(oldRow, Now));
        Assert.Null(PostLockRules.LockStart(null, null));
    }

    [Fact]
    public void Delete_inside_the_lock_is_PostLocked_even_with_members_and_even_when_private()
    {
        var r = PostLockRules.DeleteRefusal(Posted(TimeSpan.FromHours(1)), otherMembers: 2, Now)!;
        Assert.Equal(SocialErrorTypes.PostLocked, r.ErrorType);
        Assert.Equal(Now.AddHours(71), Data(r).GetProperty("unlocksAt").GetDateTime().ToUniversalTime());
        Assert.Equal(SocialErrorTypes.PostLocked, PostLockRules.DeleteRefusal(Posted(TimeSpan.FromHours(1), isPublic: false), 0, Now)!.ErrorType);
    }

    [Fact]
    public void Delete_after_the_lock_needs_the_host_alone_even_after_make_private()
    {
        var r = PostLockRules.DeleteRefusal(Posted(TimeSpan.FromDays(4), isPublic: false), otherMembers: 3, Now)!;
        Assert.Equal(SocialErrorTypes.RemoveMembersFirst, r.ErrorType);
        Assert.Equal(3, Data(r).GetProperty("count").GetInt32());
        Assert.Null(PostLockRules.DeleteRefusal(Posted(TimeSpan.FromDays(4)), otherMembers: 0, Now));
    }

    [Fact]
    public void A_never_published_trip_is_never_locked()
    {
        var plain = Posted(TimeSpan.Zero, isPublic: false, everPublished: false);
        Assert.Null(PostLockRules.DeleteRefusal(plain, otherMembers: 4, Now));
        Assert.False(PostLockRules.DeleteNeedsAlone(plain, Now));
        Assert.Null(PostLockRules.UnpublishRefusal(plain, Now));
    }

    [Fact]
    public void Make_private_follows_only_the_lock()
    {
        Assert.Equal(SocialErrorTypes.PostLocked, PostLockRules.UnpublishRefusal(Posted(TimeSpan.FromHours(2)), Now)!.ErrorType);
        Assert.Null(PostLockRules.UnpublishRefusal(Posted(TimeSpan.FromDays(3)), Now));
        Assert.Null(PostLockRules.UnpublishRefusal(Posted(TimeSpan.FromHours(2), isPublic: false), Now)); // already private
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("endPassed")]
    [InlineData("startPassedNoEnd")]
    [InlineData("windowExpired")]
    public void A_trip_that_is_over_is_exempt_from_the_alone_rule_once_the_72_hours_passed(string how)
    {
        var s = Over(how, how == "windowExpired" ? TimeSpan.FromDays(70) : TimeSpan.FromDays(4));
        Assert.True(PostLockRules.TripOver(s, Now));
        Assert.Null(PostLockRules.UnlocksAt(s, Now));
        Assert.False(PostLockRules.DeleteNeedsAlone(s, Now));
        Assert.Null(PostLockRules.DeleteRefusal(s, otherMembers: 5, Now));
        Assert.Null(PostLockRules.UnpublishRefusal(s, Now));
    }

    // Safety review 2: the trip dates are owner-editable (any date from year 2000), so "over" never lifts the 72 hours:
    // post -> move the dates into the past -> delete at once is refused.
    [Theory]
    [InlineData("completed")]
    [InlineData("endPassed")]
    [InlineData("startPassedNoEnd")]
    public void A_trip_moved_into_the_past_inside_the_72_hours_is_still_locked(string how)
    {
        var s = Over(how, TimeSpan.FromHours(1));
        Assert.True(PostLockRules.TripOver(s, Now));
        Assert.Equal(Now.AddHours(71), PostLockRules.UnlocksAt(s, Now));
        var r = PostLockRules.DeleteRefusal(s, otherMembers: 5, Now)!;
        Assert.Equal(SocialErrorTypes.PostLocked, r.ErrorType);
        Assert.Equal(SocialErrorTypes.PostLocked, PostLockRules.UnpublishRefusal(s, Now)!.ErrorType);
        Assert.Equal(SocialErrorTypes.PostLocked, PostLockRules.DeleteRefusal(s, otherMembers: 0, Now)!.ErrorType);
    }

    private static PostLockState Over(string how, TimeSpan ago) => how switch
    {
        "completed" => Posted(ago, status: TripStatuses.Completed, start: Today, end: Today.AddDays(2), window: null),
        "endPassed" => Posted(ago, start: Today.AddDays(-3), end: Today.AddDays(-1), window: null),
        "startPassedNoEnd" => Posted(ago, start: Today.AddDays(-1), window: null),
        _ => Posted(ago, window: 2),
    };

    [Fact]
    public void Completing_an_undated_or_future_trip_does_not_get_round_the_rules()
    {
        var undated = Posted(TimeSpan.FromHours(1), status: TripStatuses.Completed, window: 2);
        Assert.False(PostLockRules.TripOver(undated, Now));
        Assert.Equal(SocialErrorTypes.PostLocked, PostLockRules.DeleteRefusal(undated, 1, Now)!.ErrorType);
        var later = Posted(TimeSpan.FromDays(4), status: TripStatuses.Completed, start: Today.AddDays(5), window: null);
        Assert.Equal(SocialErrorTypes.RemoveMembersFirst, PostLockRules.DeleteRefusal(later, 1, Now)!.ErrorType);
    }

    [Fact]
    public void A_trip_on_its_last_day_is_not_over_yet()
    {
        var s = Posted(TimeSpan.FromHours(1), start: Today.AddDays(-1), end: Today, window: null);
        Assert.False(PostLockRules.TripOver(s, Now));
        Assert.NotNull(PostLockRules.DeleteRefusal(s, 0, Now));
    }

    [Fact]
    public void Owner_trip_answers_carry_the_unlock_time_and_the_alone_flag()
    {
        var trip = new Trip
        {
            Status = TripStatuses.Draft, IsPublic = true, PostWindowMonths = 2,
            PublishedAt = Now.AddHours(-1), LastPublishedAt = Now.AddHours(-1),
        };
        Assert.Equal(Now.AddHours(71), PostLockRules.UnlocksAt(PostLockRules.Of(trip), Now));
        Assert.True(PostLockRules.DeleteNeedsAlone(PostLockRules.Of(trip), Now));
        trip.IsPublic = false;                                   // made private after the lock: delete still needs the host alone
        trip.LastPublishedAt = Now.AddDays(-4);
        Assert.Null(PostLockRules.UnlocksAt(PostLockRules.Of(trip), Now));
        Assert.True(PostLockRules.DeleteNeedsAlone(PostLockRules.Of(trip), Now));
        Assert.False(PostLockRules.DeleteNeedsAlone(PostLockRules.Of(new Trip { Status = TripStatuses.Draft }), Now));

        var handlers = Src("RentNearBy.Api", "Handlers", "TripHandlers.cs");
        const string unlocks = "PostUnlocksAt = RentNearBy.Core.Models.Social.PostLockRules.UnlocksAt(RentNearBy.Core.Models.Social.PostLockRules.Of(trip), DateTime.UtcNow),";
        const string alone = "DeleteNeedsAlone = RentNearBy.Core.Models.Social.PostLockRules.DeleteNeedsAlone(RentNearBy.Core.Models.Social.PostLockRules.Of(trip), DateTime.UtcNow),";
        Assert.Equal(2, handlers.Split(unlocks).Length - 1);     // TripDto (owner answers) and TripSummaryDto (My Trips)
        Assert.Equal(2, handlers.Split(alone).Length - 1);
    }

    [Fact]
    public void Every_owner_remove_path_checks_the_lock_and_the_admin_path_never_does()
    {
        var delete = Src("RentNearBy.Api", "Handlers", "TripHandlers.cs");
        var lockAt = delete.IndexOf("await TripGuards.LockTripAsync(db, trip.Id);", StringComparison.Ordinal);
        var checkAt = delete.IndexOf("PostLockRules.DeleteRefusal(", StringComparison.Ordinal);
        var closeAt = delete.IndexOf("pushes = await closure.OnTripDeletedAsync(trip.Id);", StringComparison.Ordinal);
        Assert.True(lockAt > 0 && checkAt > lockAt, "the delete rule runs under the trip row lock");
        Assert.True(closeAt > checkAt, "nothing is closed or deleted before the rule");
        var posts = Src("RentNearBy.Infrastructure", "Services", "Social", "PublicTripService.cs");
        Assert.Contains("if (ownerId is not null)", posts);
        Assert.Contains("PostLockRules.UnpublishRefusal(found.State, Now)", posts);
        Assert.Contains("if (firstPublish) trip.LastPublishedAt = nowUtc;", posts);       // edit never restarts the lock
        Assert.Contains("FOR UPDATE", PostLockReads.LockSql);
        Assert.Contains("'Joined'", PostLockReads.ReadSql);
        var admin = Src("RentNearBy.Api", "Handlers", "Social", "SocialAdminHandlers.cs");
        Assert.Contains("posts.UnpublishAsync(null, tripId, ct)", admin);
        // System paths never go through the owner rules: the plan reconcile and account deletion use their own SQL.
        Assert.DoesNotContain("PostLock", Src("RentNearBy.Infrastructure", "Services", "Social", "SeekerPlanReconcile.cs"));
        var hooks = Src("RentNearBy.Infrastructure", "Services", "Buddy", "Be2", "TripSharingHooks.cs");
        Assert.Contains("new NoticeUpsert(r, NoticeKinds.JoinDeclined, tripId, ownerId)", hooks);
    }

    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([BuddySources.Root(), .. parts]));

    [Fact]
    public void Moving_the_dates_into_the_past_is_refused_while_other_travellers_are_joined()
    {
        var running = Posted(TimeSpan.FromDays(5), start: Today.AddDays(3), end: Today.AddDays(6));
        var r = PostLockRules.DateMoveRefusal(running, 2, Today.AddDays(-4), Today.AddDays(-1), Now);
        Assert.NotNull(r);
        Assert.Equal(SocialErrorTypes.TripDatesLocked, r!.ErrorType);
        Assert.Equal(2, Data(r).GetProperty("count").GetInt32());
        Assert.Equal(409, SocialErrorTypes.StatusOf(SocialErrorTypes.TripDatesLocked));
        Assert.Contains(SocialErrorTypes.TripDatesLocked, SocialErrorTypes.All);
    }

    [Fact]
    public void Date_moves_that_stay_honest_are_allowed()
    {
        var running = Posted(TimeSpan.FromDays(5), start: Today.AddDays(3), end: Today.AddDays(6));
        Assert.Null(PostLockRules.DateMoveRefusal(running, 2, Today.AddDays(10), Today.AddDays(12), Now));      // still in the future
        Assert.Null(PostLockRules.DateMoveRefusal(running, 2, Today, Today, Now));                               // today is not the past
        Assert.Null(PostLockRules.DateMoveRefusal(running, 0, Today.AddDays(-4), Today.AddDays(-1), Now));       // host alone
        Assert.Null(PostLockRules.DateMoveRefusal(Posted(TimeSpan.FromDays(5), everPublished: false, start: Today.AddDays(3)), 2,
            Today.AddDays(-4), Today.AddDays(-1), Now));                                                          // never published
        var over = Posted(TimeSpan.FromDays(30), start: Today.AddDays(-10), end: Today.AddDays(-8));
        Assert.Null(PostLockRules.DateMoveRefusal(over, 2, Today.AddDays(-12), Today.AddDays(-9), Now));         // already over
    }

    [Fact]
    public void The_trip_save_checks_the_date_move_before_writing()
    {
        var src = File.ReadAllText(Path.Combine(BuddySources.Root(), "RentNearBy.Api", "Handlers", "TripHandlers.cs"));
        var update = src.IndexOf("public static async Task<IResult> Update(", StringComparison.Ordinal);
        var check = src.IndexOf("PostLockRules.DateMoveRefusal(", update, StringComparison.Ordinal);
        var apply = src.IndexOf("ApplyPlan(trip, plan);", update, StringComparison.Ordinal);
        Assert.True(update > 0 && check > update && check < apply);
    }
}
