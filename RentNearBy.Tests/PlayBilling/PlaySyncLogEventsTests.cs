using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.PlayBilling;

public class PlaySyncLogEventsTests
{
    [Theory]
    [InlineData("abcdefghijkl", "efghijkl")]
    [InlineData("abcdefgh", "abcdefgh")]
    [InlineData("abc", "abc")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Tail_KeepsLastEight(string? token, string expected)
        => Assert.Equal(expected, PlaySyncLogEvents.Tail(token));

    [Theory]
    [InlineData("abcdefghijkl")]
    [InlineData("abc")]
    [InlineData(null)]
    public void Tail_SameAsClientTokenTail(string? token)
        => Assert.Equal(PlayBillingClient.TokenTail(token), PlaySyncLogEvents.Tail(token));

    [Fact]
    public void EventIds_StableNamesAndIds()
    {
        Assert.Equal("play.chargeback.review", PlaySyncLogEvents.ChargebackReview.Name);
        Assert.Equal(7114, PlaySyncLogEvents.ChargebackReview.Id);
        Assert.Equal("play.sync", PlaySyncLogEvents.Sync.Name);
        Assert.Equal(7101, PlaySyncLogEvents.Sync.Id);
    }
}
