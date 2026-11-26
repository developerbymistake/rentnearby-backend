using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;

namespace RentNearBy.Tests.Social;

// PLAN_STATES_SOCIAL 1, 3, 4: the pure choice of the seeker plan reconcile per key and moment, the write rule of inactive trips
// and the end notice words.
public class SeekerPlanStatesTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);

    private static SeekerPlanRow Trip(int minute, bool inactive = false, bool isPublic = false, bool paused = false, bool open = true,
        bool grandfathered = false, bool fresh = true, int? publishedMinute = null)
        => new(new Guid($"00000000-0000-0000-0000-{minute:D12}"), T0.AddMinutes(minute),
            isPublic || paused ? T0.AddMinutes(publishedMinute ?? minute) : null, open, grandfathered, inactive, isPublic, paused, fresh);

    private static SeekerPlanTargets Apply(List<SeekerPlanRow> rows, SeekerPlanTargets t)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var inactive = t.Inactivate.Contains(r.Id) || (r.InactiveByPlan && !t.Activate.Contains(r.Id));
            var isPublic = t.RestorePosts.Contains(r.Id) || (r.IsPublic && !t.PausePosts.Contains(r.Id));
            var paused = t.PausePosts.Contains(r.Id) || (r.PausedByPlan && !t.RestorePosts.Contains(r.Id) && !t.DropPaused.Contains(r.Id));
            rows[i] = r with { InactiveByPlan = inactive, IsPublic = isPublic, PausedByPlan = paused };
        }
        return t;
    }

    [Fact]
    public void Before_free_at_or_below_the_limit_nothing_changes()
    {
        var t = SeekerPlanChoice.Choose([Trip(1, isPublic: true)], activeLimit: 1, publicLimit: 1);
        Assert.False(t.Changed);
        Assert.Equal(0, t.InactiveAfter);
    }

    [Fact]
    public void During_plus_full_keeps_all_ten_active_and_three_public()
    {
        var rows = Enumerable.Range(1, 10).Select(i => Trip(i, isPublic: i <= 3)).ToList();
        var t = SeekerPlanChoice.Choose(rows, activeLimit: 10, publicLimit: 3);
        Assert.False(t.Changed);
    }

    [Fact]
    public void After_expiry_the_last_added_trip_stays_active_and_older_ones_go_inactive()
    {
        var rows = new List<SeekerPlanRow> { Trip(1), Trip(2), Trip(3) };
        var t = SeekerPlanChoice.Choose(rows, activeLimit: 1, publicLimit: 1);
        Assert.Equal([rows[1].Id, rows[0].Id], t.Inactivate.OrderByDescending(x => x).ToList());
        Assert.DoesNotContain(rows[2].Id, t.Inactivate);
        Assert.Equal(2, t.InactiveAfter);
    }

    [Fact]
    public void After_expiry_the_newest_public_post_stays_and_older_posts_are_paused()
    {
        var rows = new List<SeekerPlanRow> { Trip(1, isPublic: true), Trip(2, isPublic: true), Trip(3, isPublic: true) };
        var t = SeekerPlanChoice.Choose(rows, activeLimit: 10, publicLimit: 1);
        Assert.Equal(2, t.PausePosts.Count);
        Assert.DoesNotContain(rows[2].Id, t.PausePosts);
        Assert.Equal(2, t.PausedAfter);
    }

    [Fact]
    public void An_inactive_trip_never_stays_public()
    {
        var rows = new List<SeekerPlanRow> { Trip(1, isPublic: true), Trip(2) };
        var t = SeekerPlanChoice.Choose(rows, activeLimit: 1, publicLimit: 1);
        Assert.Contains(rows[0].Id, t.Inactivate);
        Assert.Contains(rows[0].Id, t.PausePosts);
    }

    [Fact]
    public void Resubscribe_restores_the_inactive_trips_and_the_paused_posts()
    {
        var rows = new List<SeekerPlanRow> { Trip(1, inactive: true, paused: true), Trip(2, inactive: true), Trip(3, isPublic: true) };
        var t = SeekerPlanChoice.Choose(rows, activeLimit: 10, publicLimit: 3);
        Assert.Equal(2, t.Activate.Count);
        Assert.Equal([rows[0].Id], t.RestorePosts);
        Assert.Equal(0, t.InactiveAfter);
        Assert.Equal(0, t.PausedAfter);
    }

    [Fact]
    public void Expiry_then_resubscribe_then_expiry_is_stable_and_idempotent()
    {
        var rows = new List<SeekerPlanRow> { Trip(1, isPublic: true), Trip(2, isPublic: true), Trip(3) };
        Apply(rows, SeekerPlanChoice.Choose(rows, 1, 1));
        Assert.False(SeekerPlanChoice.Choose(rows, 1, 1).Changed);
        Assert.Equal([false, true, true], rows.Select(r => r.InactiveByPlan).Reverse().ToList());

        Apply(rows, SeekerPlanChoice.Choose(rows, 10, 3));
        Assert.All(rows, r => Assert.False(r.InactiveByPlan));
        Assert.Equal(2, rows.Count(r => r.IsPublic));
        Assert.False(SeekerPlanChoice.Choose(rows, 10, 3).Changed);
    }

    [Fact]
    public void Hold_or_grace_reads_the_numbers_it_is_given()
    {
        // Grace keeps Plus numbers (entitled), hold reads the Free ones: the choice only follows the numbers.
        var rows = new List<SeekerPlanRow> { Trip(1), Trip(2) };
        Assert.False(SeekerPlanChoice.Choose(rows, 10, 3).Changed);
        Assert.Single(SeekerPlanChoice.Choose(rows, 1, 1).Inactivate);
    }

    [Fact]
    public void Grandfathered_trips_stay_active_and_use_no_place()
    {
        var rows = new List<SeekerPlanRow> { Trip(1, grandfathered: true), Trip(2, grandfathered: true), Trip(3), Trip(4) };
        var t = SeekerPlanChoice.Choose(rows, activeLimit: 1, publicLimit: 1);
        Assert.Equal([rows[2].Id], t.Inactivate);
        var off = SeekerPlanChoice.Choose(rows, activeLimit: 1, publicLimit: 1, grandfather: false);
        Assert.Equal(3, off.Inactivate.Count);
    }

    [Fact]
    public void Completed_or_stale_rows_drop_their_plan_flags()
    {
        var rows = new List<SeekerPlanRow> { Trip(1, inactive: true, open: false), Trip(2, paused: true, fresh: false) };
        var t = SeekerPlanChoice.Choose(rows, activeLimit: 1, publicLimit: 1);
        Assert.Equal([rows[0].Id], t.Activate);
        Assert.Equal([rows[1].Id], t.DropPaused);
        Assert.Empty(t.RestorePosts);
    }

    [Fact]
    public void Stale_public_posts_are_left_alone()
    {
        var rows = new List<SeekerPlanRow> { Trip(1, isPublic: true, fresh: false), Trip(2, isPublic: true) };
        Assert.False(SeekerPlanChoice.Choose(rows, 10, 1).Changed);
    }

    [Theory]
    [InlineData("GET", "/api/v1/trips/{id:guid}", true)]
    [InlineData("DELETE", "/api/v1/trips/{id:guid}", true)]
    [InlineData("POST", "/api/v1/trips/{id:guid}/leave", true)]
    [InlineData("DELETE", "/api/v1/trips/{id:guid}/members/{userId:guid}", true)]
    [InlineData("POST", "/api/v1/trips/{tripId:guid}/payments/said-paid", true)]
    [InlineData("PUT", "/api/v1/trips/{id:guid}", false)]
    [InlineData("POST", "/api/v1/trips/{tripId:guid}/enquiries", false)]
    [InlineData("POST", "/api/v1/trips/{id:guid}/complete-plan", false)]
    [InlineData("POST", "/api/v1/trips/{tripId:guid}/taxi-request", false)]
    [InlineData("PUT", "/api/v1/trips/{tripId:guid}/expenses/{expenseId:guid}", false)]
    [InlineData("POST", "/api/v1/trips/{id:guid}/invites", false)]
    public void Inactive_trip_write_rule(string method, string pattern, bool allowed)
        => Assert.Equal(allowed, SeekerPlanStates.WriteAllowed(method, pattern));

    [Fact]
    public void Inactive_write_is_a_typed_409()
    {
        Assert.Equal(409, SocialErrorTypes.StatusOf(SocialErrorTypes.TripInactiveByPlan));
        Assert.DoesNotContain("private", SocialErrorTypes.MessageOf(SocialErrorTypes.TripInactiveByPlan), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void End_notice_words()
    {
        Assert.Equal("Plus ended. 1 trip stays active, 3 are paused. Buy Plus to restore them.", SeekerPlanCopy.Ended(1, 3));
        Assert.Equal("Plus ended. 1 trip stays active, 1 is paused. Buy Plus to restore it.", SeekerPlanCopy.Ended(1, 1));
        Assert.Equal("Bakhli Plus has ended. You are on the free plan now.", SeekerPlanCopy.Ended(1, 0));
    }

    [Fact]
    public void Effect_counts_only_what_was_newly_taken_off()
    {
        Assert.Equal(3, new SeekerPlanEffect(0, 2, 0, 1, true).Lost);
        Assert.Equal(0, new SeekerPlanEffect(2, 0, 1, 0, true).Lost);
    }

    [Fact]
    public void After_expiry_the_monthly_caps_and_switches_read_the_free_numbers()
    {
        // Join requests / new chats: the cap is the plan number, the usage is this IST month's rows; after expiry 5 used >= 1.
        var free = SeekerLimitsService.Compute([], null, paymentOn: true, held: false);
        Assert.Equal(SeekerPlanKeys.FreeDefault(SeekerPlanKeys.JoinRequestsPerMonth), free.JoinRequestsPerMonth);
        Assert.Equal(SeekerPlanKeys.FreeDefault(SeekerPlanKeys.NewChatsPerMonth), free.NewChatsPerMonth);
        Assert.False(free.GenderOnlyTrips);
        Assert.False(free.NearMe);
    }
}
