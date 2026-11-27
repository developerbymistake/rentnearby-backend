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
    [InlineData(ListingKinds.Stay, "A stay that was approved or is under review blocks this role change.")]
    [InlineData(ListingKinds.Taxi, "A taxi that was approved or is under review blocks this role change.")]
    [InlineData("Other", "A listing that was approved or is under review blocks this role change.")]
    public void LiveListingBlockedMessage_PerKind(string kind, string expected)
        => Assert.Equal(expected, RoleChangeRules.LiveListingBlockedMessage(kind));

    [Theory]
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

    // Social next item 2: only a Traveller is refused; owners keep their role request.
    [Fact]
    public void CanRequestChange_SeekerRefused_OwnersAllowed()
    {
        Assert.False(RoleChangeRules.CanRequestChange(UserRoles.SeekerId));
        Assert.True(RoleChangeRules.CanRequestChange(UserRoles.StayOwnerId));
        Assert.True(RoleChangeRules.CanRequestChange(UserRoles.TaxiDriverId));
    }

    // R4: the Traveller refusal lives only in the role request; the first role choice and the one-time correction keep their rules.
    [Fact]
    public void CanRequestChange_GuardsOnlyTheRoleRequest()
    {
        var root = RentNearBy.Tests.Buddy.Foundation.BuddySources.Root();
        var src = File.ReadAllText(Path.Combine(root, "RentNearBy.Api", "Handlers", "RoleHandlers.cs"));
        var request = src.IndexOf("public static async Task<IResult> CreateRoleRequest(", StringComparison.Ordinal);
        var next = src.IndexOf("private static async Task PublishRoleRequestedAsync(", StringComparison.Ordinal);
        Assert.True(request > 0 && next > request);
        var guard = src.IndexOf("RoleChangeRules.CanRequestChange(", StringComparison.Ordinal);
        Assert.InRange(guard, request, next);
        Assert.Equal(guard, src.LastIndexOf("RoleChangeRules.CanRequestChange(", StringComparison.Ordinal));
    }

    [Fact]
    public void NotAllowed_IsATypedCalmAnswer()
    {
        Assert.Equal("RoleChangeNotAllowed", RoleChangeRules.NotAllowedErrorType);
        Assert.Equal("Traveller accounts cannot change role.", RoleChangeRules.NotAllowedMessage);
    }
}
