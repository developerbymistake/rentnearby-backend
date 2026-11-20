using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Tests.Buddy.Be1;

public class BuddyRulesTests
{
    private static readonly DateTime Now = new(2026, 5, 16, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Me = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Him = Guid.Parse("55555555-5555-4555-8555-555555555555");

    private static RequestFacts Mine(string status, int createdDaysAgo = 1, bool blockedSender = false)
    {
        var created = Now.AddDays(-createdDaysAgo);
        return new RequestFacts(Guid.NewGuid(), Me, status, created, created + BuddyLimits.RequestExpiry, blockedSender);
    }

    [Theory]
    [InlineData("Bakhli#29583047", "Bakhli#29583047")]
    [InlineData("  bakhli#29583047 ", "Bakhli#29583047")]
    [InlineData("BAKHLI#29583047", "Bakhli#29583047")]
    [InlineData("Bakhli#09583047", null)]
    [InlineData("Bakhli#2958304", null)]
    [InlineData("Bakhli#295830471", null)]
    [InlineData("Bakhli29583047", null)]
    [InlineData("29583047", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Bakhli#2958304a", null)]
    public void NormalizeFind_accepts_only_exact_usernames(string? query, string? expected)
        => Assert.Equal(expected, BuddyRules.NormalizeFind(query));

    [Fact]
    public void Impossible_username_is_never_a_real_one() => Assert.Null(BuddyRules.NormalizeFind(BuddyRules.ImpossibleUsername));

    [Fact]
    public void Relation_self_wins() => Assert.Equal(BuddyRelations.Self, BuddyRules.Relation(Me, Me, true, null, null, Now).Relation);

    [Fact]
    public void Relation_buddy() => Assert.Equal(BuddyRelations.Buddy, BuddyRules.Relation(Me, Him, true, Mine("Accepted"), null, Now).Relation);

    [Fact]
    public void Relation_none_without_history() => Assert.Equal(BuddyRelations.None, BuddyRules.Relation(Me, Him, false, null, null, Now).Relation);

    [Fact]
    public void Relation_requested_by_me_carries_id_and_expiry()
    {
        var mine = Mine(BuddyRequestStatuses.Pending);
        var r = BuddyRules.Relation(Me, Him, false, mine, null, Now);
        Assert.Equal(BuddyRelations.RequestedByMe, r.Relation);
        Assert.Equal(mine.Id, r.RequestId);
        Assert.Equal(mine.ExpiresAt, r.ExpiresAt);
    }

    [Theory]
    [InlineData(BuddyRequestStatuses.Declined, false)]
    [InlineData(BuddyRequestStatuses.ClosedByBlock, true)]
    public void A_silent_end_still_reads_requested_before_expiry(string status, bool blockedSender)
        => Assert.Equal(BuddyRelations.RequestedByMe, BuddyRules.Relation(Me, Him, false, Mine(status, 3, blockedSender), null, Now).Relation);

    [Theory]
    [InlineData(BuddyRequestStatuses.Declined)]
    [InlineData(BuddyRequestStatuses.Expired)]
    [InlineData(BuddyRequestStatuses.Cancelled)]
    [InlineData(BuddyRequestStatuses.Pending)]
    public void After_expiry_any_end_is_cooldown_until_30_days(string status)
    {
        Assert.Equal(BuddyRelations.Cooldown, BuddyRules.Relation(Me, Him, false, Mine(status, 15), null, Now).Relation);
        Assert.Equal(BuddyRelations.None, BuddyRules.Relation(Me, Him, false, Mine(status, 31), null, Now).Relation);
    }

    [Fact]
    public void Cancel_inside_14_days_is_cooldown_at_once()
        => Assert.Equal(BuddyRelations.Cooldown, BuddyRules.Relation(Me, Him, false, Mine(BuddyRequestStatuses.Cancelled, 1), null, Now).Relation);

    [Fact]
    public void Block_closed_where_I_blocked_is_cooldown_not_waiting()
        => Assert.Equal(BuddyRelations.Cooldown,
            BuddyRules.Relation(Me, Him, false, Mine(BuddyRequestStatuses.ClosedByBlock, 1, blockedSender: false), null, Now).Relation);

    [Fact]
    public void An_accepted_request_never_cools_down_after_a_remove()
        => Assert.Equal(BuddyRelations.None, BuddyRules.Relation(Me, Him, false, Mine(BuddyRequestStatuses.Accepted, 2), null, Now).Relation);

    [Fact]
    public void Their_pending_request_reads_requested_me()
    {
        var theirs = Guid.NewGuid();
        var r = BuddyRules.Relation(Me, Him, false, null, theirs, Now);
        Assert.Equal(BuddyRelations.RequestedMe, r.Relation);
        Assert.Equal(theirs, r.RequestId);
        Assert.Null(r.ExpiresAt);
    }

    [Fact]
    public void Their_request_wins_over_my_cooldown()
        => Assert.Equal(BuddyRelations.RequestedMe,
            BuddyRules.Relation(Me, Him, false, Mine(BuddyRequestStatuses.Cancelled, 2), Guid.NewGuid(), Now).Relation);

    [Fact]
    public void Sent_state_is_waiting_until_expiry_then_ended()
    {
        Assert.Equal(SentStates.Waiting, BuddyRules.SentState(Now.AddMinutes(1), Now));
        Assert.Equal(SentStates.Ended, BuddyRules.SentState(Now, Now));
    }

    [Theory]
    [InlineData(BuddyRequestStatuses.Pending, false, 1, true)]
    [InlineData(BuddyRequestStatuses.Declined, false, 1, true)]
    [InlineData(BuddyRequestStatuses.Expired, false, -6, true)]
    [InlineData(BuddyRequestStatuses.Expired, false, -8, false)]
    [InlineData(BuddyRequestStatuses.ClosedByBlock, true, 1, true)]
    [InlineData(BuddyRequestStatuses.ClosedByBlock, false, 1, false)]
    [InlineData(BuddyRequestStatuses.Cancelled, false, 1, false)]
    [InlineData(BuddyRequestStatuses.Accepted, false, 1, false)]
    [InlineData(BuddyRequestStatuses.ClosedByDeletion, false, 1, false)]
    public void Shown_in_sent(string status, bool blockedSender, int expiresInDays, bool shown)
        => Assert.Equal(shown, BuddyRules.ShownInSent(status, blockedSender, Now.AddDays(expiresInDays), Now));

    [Fact]
    public void Trip_state_hides_a_recent_decline()
    {
        Assert.Equal(BuddyTripStates.Joined, BuddyRules.TripState(TripMemberStatuses.Joined, null, null, Now));
        Assert.Equal(BuddyTripStates.Invited, BuddyRules.TripState(TripMemberStatuses.Invited, Now.AddDays(1), null, Now));
        Assert.Equal(BuddyTripStates.None, BuddyRules.TripState(TripMemberStatuses.Invited, Now.AddDays(-1), null, Now));
        Assert.Equal(BuddyTripStates.Invited, BuddyRules.TripState(TripMemberStatuses.Declined, null, Now.AddDays(-6), Now));
        Assert.Equal(BuddyTripStates.None, BuddyRules.TripState(TripMemberStatuses.Declined, null, Now.AddDays(-8), Now));
        Assert.Equal(BuddyTripStates.None, BuddyRules.TripState(TripMemberStatuses.Left, null, null, Now));
        Assert.Equal(BuddyTripStates.None, BuddyRules.TripState(null, null, null, Now));
    }

    [Fact]
    public void Hold_must_be_cleared_or_inside_a_year()
    {
        Assert.True(BuddyRules.IsValidHold(null, Now));
        Assert.True(BuddyRules.IsValidHold(Now.AddDays(30), Now));
        Assert.False(BuddyRules.IsValidHold(Now.AddMinutes(-1), Now));
        Assert.False(BuddyRules.IsValidHold(Now.AddDays(366), Now));
    }

    [Fact]
    public void Report_note_is_kept_only_for_other()
    {
        Assert.Equal("He spams", BuddyRules.ReportDetails(BuddyRules.UserReasonOtherId, " He spams "));
        Assert.Equal(string.Empty, BuddyRules.ReportDetails(Guid.Parse("c2000000-0000-0000-0000-000000000001"), "x"));
    }
}
