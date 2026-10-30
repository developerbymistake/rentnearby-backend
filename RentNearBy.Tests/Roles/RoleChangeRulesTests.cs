namespace RentNearBy.Tests.Roles;

public class RoleChangeRulesTests
{
    private readonly FakeClock _clock = new(Dates.Utc(2026, 10, 15));

    [Fact]
    public void NextRequestAllowedAt_NeverReviewed_Null()
        => Assert.Null(RoleChangeRules.NextRequestAllowedAt(null, null, _clock.UtcNow));

    [Fact]
    public void NextRequestAllowedAt_ApprovedWithinThirtyDays_ReviewPlusThirty()
        => Assert.Equal(Dates.Utc(2026, 10, 31),
            RoleChangeRules.NextRequestAllowedAt(RoleChangeRequestStatuses.Approved, Dates.Utc(2026, 10, 1), _clock.UtcNow));

    [Fact]
    public void NextRequestAllowedAt_ApprovedCooldownOver_Null()
    {
        _clock.Advance(TimeSpan.FromDays(17));

        Assert.Null(RoleChangeRules.NextRequestAllowedAt(RoleChangeRequestStatuses.Approved, Dates.Utc(2026, 10, 1), _clock.UtcNow));
    }

    [Fact]
    public void NextRequestAllowedAt_RejectedSevenDaysPassed_Null()
        => Assert.Null(RoleChangeRules.NextRequestAllowedAt(RoleChangeRequestStatuses.Rejected, Dates.Utc(2026, 10, 1), _clock.UtcNow));

    [Fact]
    public void NextRequestAllowedAt_RejectedWithinSevenDays_ReviewPlusSeven()
        => Assert.Equal(Dates.Utc(2026, 10, 17),
            RoleChangeRules.NextRequestAllowedAt(RoleChangeRequestStatuses.Rejected, Dates.Utc(2026, 10, 10), _clock.UtcNow));

    [Fact]
    public void NextRequestAllowedAt_ExactlyAtBoundary_Null()
        => Assert.Null(RoleChangeRules.NextRequestAllowedAt(RoleChangeRequestStatuses.Rejected, Dates.Utc(2026, 10, 8), _clock.UtcNow));

    [Theory]
    [InlineData(RoleChangeRequestStatuses.Pending)]
    [InlineData(null)]
    [InlineData("OTHER")]
    public void NextRequestAllowedAt_NoCooldownStatus_Null(string? status)
        => Assert.Null(RoleChangeRules.NextRequestAllowedAt(status, Dates.Utc(2026, 10, 14), _clock.UtcNow));

    [Fact]
    public void CooldownMessage_ShowsIstTime()
        => Assert.Equal("You can send a new request after 31 Oct 2026, 5:30 AM.",
            RoleChangeRules.CooldownMessage(Dates.Utc(2026, 10, 31)));

    [Fact]
    public void CooldownMessage_UnspecifiedKind_TreatedAsUtc()
        => Assert.Equal("You can send a new request after 1 Nov 2026, 2:00 AM.",
            RoleChangeRules.CooldownMessage(new DateTime(2026, 10, 31, 20, 30, 0, DateTimeKind.Unspecified)));

    [Theory]
    [InlineData(ListingKinds.Room, "Live rooms or a pending Go Live request block this role change.")]
    [InlineData(ListingKinds.Plot, "Live plots or a pending Go Live request block this role change.")]
    [InlineData(ListingKinds.Stay, "A stay that was approved or is under review blocks this role change.")]
    [InlineData(ListingKinds.Taxi, "A taxi that was approved or is under review blocks this role change.")]
    public void LiveListingBlockedMessage_PerKind(string kind, string expected)
        => Assert.Equal(expected, RoleChangeRules.LiveListingBlockedMessage(kind));

    [Fact]
    public void RoleLockedErrorType_Constant_IsRoleLocked()
        => Assert.Equal("RoleLocked", RoleChangeRules.RoleLockedErrorType);

    [Theory]
    [InlineData(ListingKinds.Room, "Role is locked after your first approved room.")]
    [InlineData(ListingKinds.Plot, "Role is locked after your first approved plot.")]
    [InlineData(ListingKinds.Stay, "Role is locked after your first approved stay.")]
    [InlineData(ListingKinds.Taxi, "Role is locked after your first approved taxi.")]
    public void RoleLockedMessage_PerKind(string kind, string expected)
        => Assert.Equal(expected, RoleChangeRules.RoleLockedMessage(kind));

    [Theory]
    [InlineData(ListingKinds.Room, true, null, true)]
    [InlineData(ListingKinds.Room, false, null, false)]
    [InlineData(ListingKinds.Plot, true, null, true)]
    [InlineData(ListingKinds.Plot, false, "Approved", false)]
    [InlineData(ListingKinds.Stay, false, StayStatuses.PendingReview, true)]
    [InlineData(ListingKinds.Stay, false, StayStatuses.Approved, true)]
    [InlineData(ListingKinds.Stay, true, StayStatuses.Rejected, true)]
    [InlineData(ListingKinds.Stay, false, StayStatuses.Draft, false)]
    [InlineData(ListingKinds.Stay, false, null, false)]
    [InlineData(ListingKinds.Taxi, false, TaxiStatuses.PendingReview, true)]
    [InlineData(ListingKinds.Taxi, true, null, true)]
    [InlineData(ListingKinds.Taxi, false, TaxiStatuses.Rejected, false)]
    [InlineData(null, true, "Approved", false)]
    public void IsLocked_PerKindAndState_Expected(string? kind, bool everApproved, string? status, bool expected)
        => Assert.Equal(expected, RoleChangeRules.IsLocked(kind, everApproved, status));
}
