using System.Text.Json;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Social;

// Owner order 2026-11-28 (I-3): no post lock; every trip with other joined travellers is deleted only after they were removed or
// left (no "over" exemption); the date-move rule keeps its first scope (ever-published trips). Pure rules + wiring pins.
public class PostLockRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = IstTime.TodayIst(Now);

    private static PostLockState Posted(TimeSpan ago, bool isPublic = true, string status = TripStatuses.Draft, DateOnly? start = null,
        DateOnly? end = null, short? window = 2)
        => new(isPublic, Now - ago, Now - ago, status, start, end, window);

    private static PostLockState Plain(string status = TripStatuses.Draft, DateOnly? start = null, DateOnly? end = null)
        => new(false, null, null, status, start, end, null);

    private static JsonElement Data(PostLockRefusal r) => JsonSerializer.SerializeToElement(r.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void RemoveMembersFirst_is_a_409_and_not_a_plan_cap()
    {
        Assert.Equal(409, SocialErrorTypes.StatusOf(SocialErrorTypes.RemoveMembersFirst));
        Assert.Contains(SocialErrorTypes.RemoveMembersFirst, SocialErrorTypes.All);
        Assert.False(SocialErrorTypes.IsCap(SocialErrorTypes.RemoveMembersFirst));
    }

    [Fact]
    public void There_is_no_post_lock_any_more()
    {
        var justPosted = Posted(TimeSpan.FromMinutes(1));
        Assert.Null(PostLockRules.UnlocksAt(justPosted, Now));
        Assert.Null(PostLockRules.DeleteRefusal(justPosted, otherMembers: 0, Now));          // alone: delete at once
        Assert.Null(PostLockRules.DeleteRefusal(Posted(TimeSpan.FromMinutes(1), isPublic: false), 0, Now));
    }

    [Fact]
    public void A_post_with_other_travellers_needs_them_removed_first()
    {
        var r = PostLockRules.DeleteRefusal(Posted(TimeSpan.FromHours(1)), otherMembers: 3, Now)!;
        Assert.Equal(SocialErrorTypes.RemoveMembersFirst, r.ErrorType);
        Assert.Equal(3, Data(r).GetProperty("count").GetInt32());
        Assert.Equal(SocialErrorTypes.RemoveMembersFirst,
            PostLockRules.DeleteRefusal(Posted(TimeSpan.FromDays(4), isPublic: false), otherMembers: 1, Now)!.ErrorType);
    }

    [Fact]
    public void A_never_published_trip_with_travellers_needs_them_removed_first_too()
    {
        var plain = Plain(start: Today.AddDays(5), end: Today.AddDays(7));
        Assert.True(PostLockRules.DeleteNeedsAlone(plain, Now));
        var r = PostLockRules.DeleteRefusal(plain, otherMembers: 2, Now)!;
        Assert.Equal(SocialErrorTypes.RemoveMembersFirst, r.ErrorType);
        Assert.Equal(2, Data(r).GetProperty("count").GetInt32());
        Assert.Null(PostLockRules.DeleteRefusal(plain, otherMembers: 0, Now));
        Assert.NotNull(PostLockRules.DeleteRefusal(Plain(), otherMembers: 1, Now));          // an undated normal trip is never over
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("endPassed")]
    [InlineData("startPassedNoEnd")]
    [InlineData("windowExpired")]
    public void A_trip_that_is_over_still_needs_the_others_removed_first(string how)
    {
        var s = Over(how, how == "windowExpired" ? TimeSpan.FromDays(70) : TimeSpan.FromHours(1));
        Assert.True(PostLockRules.TripOver(s, Now));
        Assert.True(PostLockRules.DeleteNeedsAlone(s, Now));
        Assert.Equal(SocialErrorTypes.RemoveMembersFirst, PostLockRules.DeleteRefusal(s, otherMembers: 5, Now)!.ErrorType);
        Assert.Null(PostLockRules.DeleteRefusal(s, otherMembers: 0, Now));
    }

    [Fact]
    public void An_over_normal_trip_needs_them_removed_too()
    {
        var s = Plain(start: Today.AddDays(-3), end: Today.AddDays(-1));
        Assert.True(PostLockRules.TripOver(s, Now));
        Assert.Equal(SocialErrorTypes.RemoveMembersFirst, PostLockRules.DeleteRefusal(s, otherMembers: 2, Now)!.ErrorType);
    }

    private static PostLockState Over(string how, TimeSpan ago) => how switch
    {
        "completed" => Posted(ago, status: TripStatuses.Completed, start: Today, end: Today.AddDays(2), window: null),
        "endPassed" => Posted(ago, start: Today.AddDays(-3), end: Today.AddDays(-1), window: null),
        "startPassedNoEnd" => Posted(ago, start: Today.AddDays(-1), window: null),
        _ => Posted(ago, window: 2),
    };

    [Fact]
    public void Completing_an_undated_or_future_trip_does_not_get_round_the_rule()
    {
        var undated = Posted(TimeSpan.FromHours(1), status: TripStatuses.Completed, window: 2);
        Assert.False(PostLockRules.TripOver(undated, Now));
        Assert.Equal(SocialErrorTypes.RemoveMembersFirst, PostLockRules.DeleteRefusal(undated, 1, Now)!.ErrorType);
        var later = Plain(TripStatuses.Completed, start: Today.AddDays(5));
        Assert.Equal(SocialErrorTypes.RemoveMembersFirst, PostLockRules.DeleteRefusal(later, 1, Now)!.ErrorType);
    }

    [Fact]
    public void A_trip_on_its_last_day_is_not_over_yet()
    {
        var s = Plain(start: Today.AddDays(-1), end: Today);
        Assert.False(PostLockRules.TripOver(s, Now));
        Assert.NotNull(PostLockRules.DeleteRefusal(s, 1, Now));
    }

    [Fact]
    public void Owner_trip_answers_carry_no_unlock_time_and_the_alone_flag()
    {
        var trip = new Trip
        {
            Status = TripStatuses.Draft, IsPublic = true, PostWindowMonths = 2,
            PublishedAt = Now.AddHours(-1), LastPublishedAt = Now.AddHours(-1),
        };
        Assert.Null(PostLockRules.UnlocksAt(PostLockRules.Of(trip), Now));
        Assert.True(PostLockRules.DeleteNeedsAlone(PostLockRules.Of(trip), Now));
        Assert.True(PostLockRules.DeleteNeedsAlone(PostLockRules.Of(new Trip { Status = TripStatuses.Draft }), Now));

        var handlers = Src("RentNearBy.Api", "Handlers", "TripHandlers.cs");
        const string unlocks = "PostUnlocksAt = RentNearBy.Core.Models.Social.PostLockRules.UnlocksAt(RentNearBy.Core.Models.Social.PostLockRules.Of(trip), DateTime.UtcNow),";
        const string alone = "DeleteNeedsAlone = RentNearBy.Core.Models.Social.PostLockRules.DeleteNeedsAlone(RentNearBy.Core.Models.Social.PostLockRules.Of(trip), DateTime.UtcNow),";
        Assert.Equal(2, handlers.Split(unlocks).Length - 1);     // TripDto (owner answers) and TripSummaryDto (My Trips)
        Assert.Equal(2, handlers.Split(alone).Length - 1);
    }

    [Fact]
    public void The_owner_delete_checks_the_rule_under_the_trip_lock_for_every_trip()
    {
        var delete = Src("RentNearBy.Api", "Handlers", "TripHandlers.cs");
        var lockAt = delete.IndexOf("await TripGuards.LockTripAsync(db, trip.Id);", StringComparison.Ordinal);
        var checkAt = delete.IndexOf("PostLockRules.DeleteRefusal(", StringComparison.Ordinal);
        var closeAt = delete.IndexOf("pushes = await closure.OnTripDeletedAsync(trip.Id);", StringComparison.Ordinal);
        Assert.True(lockAt > 0 && checkAt > lockAt, "the delete rule runs under the trip row lock");
        Assert.True(closeAt > checkAt, "nothing is closed or deleted before the rule");
        Assert.Contains("'Joined'", PostLockReads.ReadSql);
        Assert.Contains("FOR UPDATE", PostLockReads.LockSql);
        // System paths never go through the owner rules: the plan reconcile and account deletion use their own SQL.
        Assert.DoesNotContain("PostLock", Src("RentNearBy.Infrastructure", "Services", "Social", "SeekerPlanReconcile.cs"));
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
        Assert.Null(PostLockRules.DateMoveRefusal(Plain(start: Today.AddDays(3)), 2, Today.AddDays(-4), Today.AddDays(-1), Now)); // never published
        var over = Posted(TimeSpan.FromDays(30), start: Today.AddDays(-10), end: Today.AddDays(-8));
        Assert.Null(PostLockRules.DateMoveRefusal(over, 2, Today.AddDays(-12), Today.AddDays(-9), Now));         // already over
    }

    [Fact]
    public void The_trip_save_checks_the_date_move_before_writing_for_published_trips()
    {
        var src = File.ReadAllText(Path.Combine(BuddySources.Root(), "RentNearBy.Api", "Handlers", "TripHandlers.cs"));
        var update = src.IndexOf("public static async Task<IResult> Update(", StringComparison.Ordinal);
        var check = src.IndexOf("PostLockRules.DateMoveRefusal(", update, StringComparison.Ordinal);
        var apply = src.IndexOf("ApplyPlan(trip, plan);", update, StringComparison.Ordinal);
        Assert.True(update > 0 && check > update && check < apply);
        Assert.Contains("if (trip.PublishedAt is not null", src.Substring(update, check - update));
    }
}
