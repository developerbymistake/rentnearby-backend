using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Tests.Buddy.Be2;

public class TripSharingRulesTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Aman = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTime Now = new(2026, 5, 10, 6, 0, 0, DateTimeKind.Utc);

    private static InviteCandidate Candidate(
        bool exists = true, bool active = true, bool buddy = true, bool blocked = false,
        string? status = null, DateTime? expires = null, DateTime? responded = null, Guid? id = null)
        => new(id ?? Aman, exists, active, buddy, blocked, status, expires, responded);

    [Fact]
    public void Invite_expiry_is_end_of_last_trip_day_ist_when_earlier_than_14_days()
        => Assert.Equal(new DateTime(2026, 5, 22, 18, 29, 59, DateTimeKind.Utc), TripSharingRules.InviteExpiresAt(Now, new DateOnly(2026, 5, 22)));

    [Fact]
    public void Invite_expiry_is_14_days_without_dates_or_for_a_far_trip()
    {
        Assert.Equal(Now.AddDays(14), TripSharingRules.InviteExpiresAt(Now, null));
        Assert.Equal(Now.AddDays(14), TripSharingRules.InviteExpiresAt(Now, new DateOnly(2026, 9, 1)));
    }

    [Fact]
    public void Classify_a_good_buddy_is_invited() => Assert.Equal(InviteOutcomes.Invited, TripSharingRules.Classify(Candidate(), Owner, Now));

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void Classify_every_cannot_reason_reads_NotBuddy(bool exists, bool active, bool buddy, bool blocked)
        => Assert.Equal(InviteOutcomes.NotBuddy, TripSharingRules.Classify(Candidate(exists, active, buddy, blocked), Owner, Now));

    [Fact]
    public void Classify_the_owner_himself_is_NotBuddy()
        => Assert.Equal(InviteOutcomes.NotBuddy, TripSharingRules.Classify(Candidate(id: Owner), Owner, Now));

    [Fact]
    public void Classify_joined_is_AlreadyOnTrip()
        => Assert.Equal(InviteOutcomes.AlreadyOnTrip, TripSharingRules.Classify(Candidate(status: TripMemberStatuses.Joined), Owner, Now));

    [Fact]
    public void Classify_waiting_invite_is_AlreadyInvited_but_an_overdue_one_is_invited_again()
    {
        Assert.Equal(InviteOutcomes.AlreadyInvited, TripSharingRules.Classify(Candidate(status: TripMemberStatuses.Invited, expires: Now.AddDays(1)), Owner, Now));
        Assert.Equal(InviteOutcomes.Invited, TripSharingRules.Classify(Candidate(status: TripMemberStatuses.Invited, expires: Now.AddMinutes(-1)), Owner, Now));
    }

    [Fact]
    public void Classify_decline_inside_7_days_is_AlreadyInvited_after_7_days_invited()
    {
        Assert.Equal(InviteOutcomes.AlreadyInvited, TripSharingRules.Classify(Candidate(status: TripMemberStatuses.Declined, responded: Now.AddDays(-6)), Owner, Now));
        Assert.Equal(InviteOutcomes.Invited, TripSharingRules.Classify(Candidate(status: TripMemberStatuses.Declined, responded: Now.AddDays(-8)), Owner, Now));
    }

    [Theory]
    [InlineData(TripMemberStatuses.Left)]
    [InlineData(TripMemberStatuses.Removed)]
    [InlineData(TripMemberStatuses.Cancelled)]
    [InlineData(TripMemberStatuses.Expired)]
    public void Classify_former_and_ended_rows_can_be_invited_again(string status)
        => Assert.Equal(InviteOutcomes.Invited, TripSharingRules.Classify(Candidate(status: status, expires: Now.AddDays(-1)), Owner, Now));

    [Theory]
    [InlineData(0, 0, 11)]
    [InlineData(2, 1, 8)]
    [InlineData(11, 0, 0)]
    [InlineData(10, 5, 0)]
    public void Seats_left_counts_joined_viewers_and_waiting_invites(int joined, int waiting, int expected)
        => Assert.Equal(expected, TripSharingRules.SeatsLeft(joined, waiting));

    [Fact]
    public void Option_order_is_ontrip_invitesent_noplaces_full_available()
    {
        Assert.Equal(InviteOptions.OnTrip, TripSharingRules.Option(TripMemberStatuses.Joined, null, null, 0, 11, 0, Now));
        Assert.Equal(InviteOptions.InviteSent, TripSharingRules.Option(TripMemberStatuses.Invited, Now.AddDays(1), null, 0, 11, 0, Now));
        Assert.Equal(InviteOptions.InviteSent, TripSharingRules.Option(TripMemberStatuses.Declined, null, Now.AddDays(-1), 5, 0, 0, Now));
        Assert.Equal(InviteOptions.NoPlaces, TripSharingRules.Option(null, null, null, 0, 0, 0, Now));
        Assert.Equal(InviteOptions.Full, TripSharingRules.Option(null, null, null, 5, 10, 1, Now));
        Assert.Equal(InviteOptions.Available, TripSharingRules.Option(TripMemberStatuses.Left, null, null, 5, 2, 0, Now));
    }

    [Fact]
    public void Ended_reason_per_status()
    {
        Assert.Equal(InviteEndedReasons.Expired, TripSharingRules.EndedReason(TripMemberStatuses.Invited, Now.AddSeconds(-1), Now));
        Assert.Equal(InviteEndedReasons.Expired, TripSharingRules.EndedReason(TripMemberStatuses.Expired, Now, Now));
        Assert.Equal(InviteEndedReasons.Cancelled, TripSharingRules.EndedReason(TripMemberStatuses.Cancelled, Now, Now));
        Assert.Equal(InviteEndedReasons.Declined, TripSharingRules.EndedReason(TripMemberStatuses.Declined, Now, Now));
        Assert.Equal(InviteEndedReasons.Unknown, TripSharingRules.EndedReason(TripMemberStatuses.Left, Now, Now));
        Assert.Equal(InviteEndedReasons.Unknown, TripSharingRules.EndedReason(TripMemberStatuses.Removed, Now, Now));
    }

    [Fact]
    public void Trip_end_rules_use_status_and_ist_date()
    {
        var today = new DateOnly(2026, 5, 10);
        Assert.True(TripSharingRules.IsEnded(TripStatuses.Completed, null, today));
        Assert.True(TripSharingRules.IsEnded(TripStatuses.Planned, today.AddDays(-1), today));
        Assert.False(TripSharingRules.IsEnded(TripStatuses.Planned, today, today));
        Assert.True(TripSharingRules.IsUpcoming(TripStatuses.Draft, null, today));
    }

    [Fact]
    public void ETag_changes_with_each_part_and_matches_weak_and_strong_forms()
    {
        var at = new DateTime(2026, 5, 16, 5, 12, 0, DateTimeKind.Utc);
        var tag = TripSharingRules.ViewETag(at, 3, 2, 99);
        Assert.Equal($"W/\"{at.Ticks}-3-2-99\"", tag);
        Assert.NotEqual(tag, TripSharingRules.ViewETag(at, 4, 2, 99));
        Assert.NotEqual(tag, TripSharingRules.ViewETag(at, 3, 3, 99));
        Assert.NotEqual(tag, TripSharingRules.ViewETag(at, 3, 2, 100));
        Assert.NotEqual(tag, TripSharingRules.ViewETag(at.AddTicks(10), 3, 2, 99));
        Assert.True(TripSharingRules.ETagMatches(tag, tag));
        Assert.True(TripSharingRules.ETagMatches($"\"{at.Ticks}-3-2-99\"", tag));
        Assert.True(TripSharingRules.ETagMatches("\"x\", " + tag, tag));
        Assert.False(TripSharingRules.ETagMatches(null, tag));
        Assert.False(TripSharingRules.ETagMatches("W/\"other\"", tag));
    }

    [Fact]
    public void ETag_moves_when_a_shown_face_changes_and_stays_when_nobody_has_one()
    {
        var at = new DateTime(2026, 5, 16, 5, 12, 0, DateTimeKind.Utc);
        var none = TripSharingRules.FaceStamp([null, null, ""]);
        Assert.Equal(0u, none);
        Assert.Equal(TripSharingRules.ViewETag(at, 3, 2, 99), TripSharingRules.ViewETag(at, 3, 2, 99, none));
        var a = TripSharingRules.FaceStamp(["https://x/a.jpg", null]);
        var b = TripSharingRules.FaceStamp(["https://x/b.jpg", null]);
        Assert.NotEqual(0u, a);
        Assert.NotEqual(a, b);
        Assert.Equal(a, TripSharingRules.FaceStamp(["https://x/a.jpg", null]));
        Assert.NotEqual(a, TripSharingRules.FaceStamp([null, "https://x/a.jpg"]));
        Assert.NotEqual(TripSharingRules.ViewETag(at, 3, 2, 99, a), TripSharingRules.ViewETag(at, 3, 2, 99, b));
        Assert.NotEqual(TripSharingRules.ViewETag(at, 3, 2, 99), TripSharingRules.ViewETag(at, 3, 2, 99, a));
    }

    [Theory]
    [InlineData(null, 7, "2026-05-10", 1)]
    [InlineData("2026-05-16", 7, "2026-05-10", 1)]
    [InlineData("2026-05-16", 7, "2026-05-19", 4)]
    [InlineData("2026-05-16", 7, "2026-06-30", 7)]
    public void Day_today_is_clamped(string? start, int days, string today, int expected)
        => Assert.Equal(expected, TripSharingRules.DayToday(start is null ? null : DateOnly.Parse(start), days, DateOnly.Parse(today)));

    [Fact]
    public void Viewer_sees_usernames_of_self_owner_and_own_buddies_only()
    {
        var other = Guid.NewGuid();
        Assert.Equal("u", TripSharingRules.VisibleUsername(true, Owner, other, false, false, "u"));
        Assert.Equal("u", TripSharingRules.VisibleUsername(false, Aman, Aman, false, false, "u"));
        Assert.Equal("u", TripSharingRules.VisibleUsername(false, Aman, Owner, true, false, "u"));
        Assert.Equal("u", TripSharingRules.VisibleUsername(false, Aman, other, false, true, "u"));
        Assert.Null(TripSharingRules.VisibleUsername(false, Aman, other, false, false, "u"));
    }

    [Theory]
    [InlineData("Suresh Kumar", "Suresh")]
    [InlineData("  Suresh ", "Suresh")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void First_word(string? name, string? expected) => Assert.Equal(expected, TripSharingRules.FirstWord(name));
}
